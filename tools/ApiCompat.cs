// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Binary-compatibility and public-API-surface checker for frameworks/rulesets that are compiled
// against one build and loaded against another (e.g. an out-of-tree ruleset DLL loaded by a client).
//
// Two modes:
//
//   API surface diff - what changed between two builds of the same assembly that can break an
//   already-compiled consumer (removed types, removed members, changed signatures):
//
//     dotnet tools/ApiCompat.cs --api <baseline.dll> <head.dll>
//
//   Reference scan - resolve every external type/member reference of a consumer DLL against the
//   assemblies it will actually load next to, and report the ones that no longer resolve:
//
//     dotnet tools/ApiCompat.cs <consumer.dll> <searchDir> [searchDir...]
//
// See the "Coverage limits" section in the README for what this deliberately does not model.

#:property Nullable=disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

if (args.Length >= 3 && args[0] == "--api")
{
    ApiDiff.Run(args[1], args[2]);
    return;
}

if (args.Length >= 2)
{
    BinaryScan.Run(args[0], args.Skip(1).ToArray());
    return;
}

Console.WriteLine("usage:");
Console.WriteLine("  dotnet ApiCompat.cs <consumer.dll> <searchDir> [searchDir...]");
Console.WriteLine("  dotnet ApiCompat.cs --api <baseline.dll> <head.dll>");

/// <summary>
/// Metadata loading and type resolution shared by both modes.
/// </summary>
internal static class Meta
{
    private static readonly Dictionary<MetadataReader, string> asm_of_reader = new Dictionary<MetadataReader, string>();
    private static readonly Dictionary<string, string> path_by_asm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MetadataReader> md_by_asm = new Dictionary<string, MetadataReader>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Tuple<PEReader, FileStream>> opened = new List<Tuple<PEReader, FileStream>>();
    private static readonly List<string> search_dirs = new List<string>();

    public static MetadataReader Open(string path)
    {
        var fs = File.OpenRead(path);
        var pe = new PEReader(fs);
        opened.Add(Tuple.Create(pe, fs));
        var md = pe.GetMetadataReader();
        asm_of_reader[md] = md.GetString(md.GetAssemblyDefinition().Name);
        return md;
    }

    public static void AddSearchDirs(IEnumerable<string> dirs)
    {
        foreach (var d in dirs)
            search_dirs.Add(Path.GetFullPath(d));
    }

    public static void AddBclSearchDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dnr = Path.Combine(pf, "dotnet", "shared", "Microsoft.NETCore.App");

        if (!Directory.Exists(dnr))
            return;

        foreach (var d in Directory.GetDirectories(dnr))
            search_dirs.Add(d);
    }

    public static string AsmOf(MetadataReader r)
    {
        if (asm_of_reader.TryGetValue(r, out var s))
            return s;

        s = r.GetString(r.GetAssemblyDefinition().Name);
        asm_of_reader[r] = s;
        return s;
    }

    public static string TypeFullName(MetadataReader md, TypeDefinition td)
    {
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();

        if (!decl.IsNil)
            return TypeFullName(md, md.GetTypeDefinition(decl)) + "+" + name;

        var ns = md.GetString(td.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public static string TypeRefFullName(MetadataReader r, TypeReferenceHandle h, bool includeAsm)
    {
        var tr = r.GetTypeReference(h);
        var name = r.GetString(tr.Name);
        var scope = tr.ResolutionScope;

        if (scope.Kind == HandleKind.TypeReference)
            return TypeRefFullName(r, (TypeReferenceHandle)scope, includeAsm) + "+" + name;

        var ns = r.GetString(tr.Namespace);
        var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

        if (scope.Kind == HandleKind.AssemblyReference)
        {
            var ar = r.GetAssemblyReference((AssemblyReferenceHandle)scope);
            return (includeAsm ? r.GetString(ar.Name) + "!" : "") + full;
        }

        return (includeAsm ? "self!" : "") + full;
    }

    public static string EntityTypeName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return TypeProv.WithAsm.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0);

            case HandleKind.TypeReference:
                return TypeProv.WithAsm.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0);

            case HandleKind.TypeSpecification:
                return reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(TypeProv.WithAsm, null);

            default:
                return "?";
        }
    }

    public static MetadataReader GetReader(string simpleName)
    {
        if (md_by_asm.TryGetValue(simpleName, out var cached))
            return cached;

        if (!path_by_asm.TryGetValue(simpleName, out var path))
        {
            path = null;

            foreach (var d in search_dirs)
            {
                var p = Path.Combine(d, simpleName + ".dll");
                if (File.Exists(p))
                {
                    path = p;
                    break;
                }
            }

            path_by_asm[simpleName] = path;
        }

        var md = path == null ? null : Open(path);
        md_by_asm[simpleName] = md;
        return md;
    }

    public static string GetResolvedPath(string simpleName)
    {
        GetReader(simpleName);
        return path_by_asm.TryGetValue(simpleName, out var p) ? p : null;
    }

    public static bool TryResolveTopLevelType(MetadataReader md, string ns, string name, out MetadataReader outMd, out TypeDefinitionHandle outTd)
        => TryResolveTopLevelType(md, ns, name, new HashSet<string>(), out outMd, out outTd);

    private static bool TryResolveTopLevelType(MetadataReader md, string ns, string name, HashSet<string> visited, out MetadataReader outMd, out TypeDefinitionHandle outTd)
    {
        outMd = null;
        outTd = default;

        if (!visited.Add(AsmOf(md) + "!" + ns + "." + name))
            return false;

        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (!td.GetDeclaringType().IsNil)
                continue;
            if (md.GetString(td.Name) == name && md.GetString(td.Namespace) == ns)
            {
                outMd = md;
                outTd = th;
                return true;
            }
        }

        // type forwarding (e.g. System.Runtime facades)
        foreach (var eth in md.ExportedTypes)
        {
            var et = md.GetExportedType(eth);
            if (et.Implementation.Kind != HandleKind.AssemblyReference)
                continue;
            if (md.GetString(et.Name) != name || md.GetString(et.Namespace) != ns)
                continue;

            var ar = md.GetAssemblyReference((AssemblyReferenceHandle)et.Implementation);
            var target = GetReader(md.GetString(ar.Name));
            if (target == null)
                return false;

            return TryResolveTopLevelType(target, ns, name, visited, out outMd, out outTd);
        }

        return false;
    }

    public static bool TryResolveTypeRef(MetadataReader r, TypeReferenceHandle h, out MetadataReader outMd, out TypeDefinitionHandle outTd)
    {
        outMd = null;
        outTd = default;

        var tr = r.GetTypeReference(h);
        var name = r.GetString(tr.Name);
        var scope = tr.ResolutionScope;

        if (scope.Kind == HandleKind.TypeReference)
        {
            if (!TryResolveTypeRef(r, (TypeReferenceHandle)scope, out var pmd, out var ptd))
                return false;

            var ptdDef = pmd.GetTypeDefinition(ptd);

            foreach (var nested in ptdDef.GetNestedTypes())
            {
                if (pmd.GetString(pmd.GetTypeDefinition(nested).Name) == name)
                {
                    outMd = pmd;
                    outTd = nested;
                    return true;
                }
            }

            return false;
        }

        MetadataReader target;

        if (scope.Kind == HandleKind.AssemblyReference)
        {
            var ar = r.GetAssemblyReference((AssemblyReferenceHandle)scope);
            target = GetReader(r.GetString(ar.Name));

            if (target == null)
                return false;
        }
        else if (scope.Kind == HandleKind.ModuleDefinition)
        {
            target = r;
        }
        else
        {
            return false;
        }

        return TryResolveTopLevelType(target, r.GetString(tr.Namespace), name, out outMd, out outTd);
    }

    public static bool TryResolveTypeDefOrRef(MetadataReader reader, int coded, out MetadataReader outMd, out TypeDefinitionHandle outTd)
    {
        outMd = null;
        outTd = default;
        var kind = coded & 3;
        var row = coded >> 2;

        switch (kind)
        {
            case 0:
                outMd = reader;
                outTd = MetadataTokens.TypeDefinitionHandle(row);
                return true;

            case 1:
                return TryResolveTypeRef(reader, MetadataTokens.TypeReferenceHandle(row), out outMd, out outTd);

            case 2:
                return TryResolveTypeSpecHead(reader, MetadataTokens.TypeSpecificationHandle(row), out outMd, out outTd);

            default:
                return false;
        }
    }

    public static bool TryResolveTypeSpecHead(MetadataReader reader, TypeSpecificationHandle h, out MetadataReader outMd, out TypeDefinitionHandle outTd)
    {
        outMd = null;
        outTd = default;

        var blob = reader.GetBlobReader(reader.GetTypeSpecification(h).Signature);
        var b = blob.ReadByte();

        if (b == 0x1F || b == 0x1D || b == 0x10)
            b = blob.ReadByte();

        if (b == 0x15)
        {
            var kind = blob.ReadByte();
            if (kind != 0x11 && kind != 0x12)
                return false;
            return TryResolveTypeDefOrRef(reader, blob.ReadCompressedInteger(), out outMd, out outTd);
        }

        if (b == 0x11 || b == 0x12)
            return TryResolveTypeDefOrRef(reader, blob.ReadCompressedInteger(), out outMd, out outTd);

        return false;
    }

    public static bool TryResolveTypeHandle(MetadataReader reader, EntityHandle handle, out MetadataReader outMd, out TypeDefinitionHandle outTd)
    {
        outMd = null;
        outTd = default;

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                outMd = reader;
                outTd = (TypeDefinitionHandle)handle;
                return true;

            case HandleKind.TypeReference:
                return TryResolveTypeRef(reader, (TypeReferenceHandle)handle, out outMd, out outTd);

            case HandleKind.TypeSpecification:
                return TryResolveTypeSpecHead(reader, (TypeSpecificationHandle)handle, out outMd, out outTd);

            default:
                return false;
        }
    }

    public static void DisposeAll()
    {
        foreach (var o in opened)
        {
            o.Item1.Dispose();
            o.Item2.Dispose();
        }
    }
}

/// <summary>
/// Formats type names inside signatures; optionally assembly-qualified so a type moved between
/// assemblies shows up as a difference.
/// </summary>
internal sealed class TypeProv : ISignatureTypeProvider<string, object>
{
    private readonly bool include_asm;

    private TypeProv(bool includeAsm) => include_asm = includeAsm;

    public static readonly TypeProv WithAsm = new TypeProv(true);
    public static readonly TypeProv NoAsm = new TypeProv(false);

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "prim:" + typeCode;
    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => (include_asm ? Meta.AsmOf(reader) + "!" : "") + Meta.TypeFullName(reader, reader.GetTypeDefinition(handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => Meta.TypeRefFullName(reader, handle, include_asm);

    public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}

/// <summary>
/// Resolves every external reference of a consumer assembly against the assemblies it will load
/// next to, reporting references that no longer resolve or whose signature changed.
/// </summary>
internal static class BinaryScan
{
    private sealed class MemberCheckResult
    {
        public string Status;   // OK | MISMATCH | MISSING
        public string Expected;
    }

    private static string MethodSig(MethodSignature<string> s, bool includeAsm)
        => s.GenericParameterCount + "|" + s.RequiredParameterCount + "|(" + string.Join(",", s.ParameterTypes) + ")->" + s.ReturnType;

    public static void Run(string targetPath, string[] searchDirArgs)
    {
        var target = Path.GetFullPath(targetPath);
        Meta.AddSearchDirs(searchDirArgs);
        Meta.AddBclSearchDirs();
        Meta.AddSearchDirs(new[] { Path.GetDirectoryName(target) });

        var reader = Meta.Open(target);
        var targetAsm = Meta.AsmOf(reader);

        Console.WriteLine("### SCAN TARGET : " + target);
        Console.WriteLine("### ASSEMBLY    : " + targetAsm + " v" + reader.GetAssemblyDefinition().Version);
        Console.WriteLine();

        Console.WriteLine("--- assembly references ---");

        foreach (var arh in reader.AssemblyReferences)
        {
            var ar = reader.GetAssemblyReference(arh);
            var n = reader.GetString(ar.Name);
            var resolvedPath = Meta.GetResolvedPath(n);
            Console.WriteLine("  " + n.PadRight(38) + " " + ar.Version.ToString().PadRight(18) + (resolvedPath ?? "(unresolved)"));
        }

        Console.WriteLine();
        Console.WriteLine("--- TypeReference resolution (external assemblies) ---");

        var typeOk = 0;
        var typeMissing = 0;
        var missingTypes = new List<string>();

        foreach (var trh in reader.TypeReferences)
        {
            var tr = reader.GetTypeReference(trh);
            var scopeHandle = tr.ResolutionScope;

            while (scopeHandle.Kind == HandleKind.TypeReference)
                scopeHandle = reader.GetTypeReference((TypeReferenceHandle)scopeHandle).ResolutionScope;

            if (scopeHandle.Kind != HandleKind.AssemblyReference)
                continue;

            var ar = reader.GetAssemblyReference((AssemblyReferenceHandle)scopeHandle);
            var asmName = reader.GetString(ar.Name);
            if (string.Equals(asmName, targetAsm, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Meta.GetReader(asmName) == null)
                continue;

            if (Meta.TryResolveTypeRef(reader, trh, out _, out _))
                typeOk++;
            else
            {
                typeMissing++;
                missingTypes.Add(asmName + "!" + Meta.TypeRefFullName(reader, trh, false));
            }
        }

        Console.WriteLine("  resolved=" + typeOk + "  MISSING=" + typeMissing);

        foreach (var t in missingTypes.Distinct().OrderBy(x => x))
            Console.WriteLine("  >> MISSING TYPE  " + t);

        Console.WriteLine();
        Console.WriteLine("--- MemberReference resolution (external assemblies) ---");

        var mOk = 0;
        var mMismatch = 0;
        var mMissing = 0;
        var issueLines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mrh in reader.MemberReferences)
        {
            var mr = reader.GetMemberReference(mrh);
            MetadataReader declMd;
            TypeDefinitionHandle declTd;

            switch (mr.Parent.Kind)
            {
                case HandleKind.TypeReference:
                    if (!Meta.TryResolveTypeRef(reader, (TypeReferenceHandle)mr.Parent, out declMd, out declTd))
                        continue;
                    break;

                case HandleKind.TypeSpecification:
                    if (!Meta.TryResolveTypeSpecHead(reader, (TypeSpecificationHandle)mr.Parent, out declMd, out declTd))
                        continue;
                    break;

                case HandleKind.TypeDefinition:
                    declMd = reader;
                    declTd = (TypeDefinitionHandle)mr.Parent;
                    break;

                default:
                    continue;
            }

            var declAsm = Meta.AsmOf(declMd);
            if (string.Equals(declAsm, targetAsm, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = reader.GetString(mr.Name);
            var isMethod = mr.GetKind() == MemberReferenceKind.Method;

            string strictSig;
            string looseSig;

            if (isMethod)
            {
                strictSig = MethodSig(mr.DecodeMethodSignature(TypeProv.WithAsm, null), true);
                looseSig = MethodSig(mr.DecodeMethodSignature(TypeProv.NoAsm, null), false);
            }
            else
            {
                strictSig = mr.DecodeFieldSignature(TypeProv.WithAsm, null);
                looseSig = mr.DecodeFieldSignature(TypeProv.NoAsm, null);
            }

            var res = CheckMember(declMd, declTd, name, isMethod, strictSig, looseSig);

            if (res.Status == "OK")
            {
                mOk++;
                continue;
            }

            if (res.Status == "MISMATCH")
                mMismatch++;
            else
                mMissing++;

            var declName = Meta.TypeFullName(declMd, declMd.GetTypeDefinition(declTd));
            var line = "  " + res.Status.PadRight(9) + declAsm + "!" + declName + "." + name + "\n" +
                       "      ref : " + looseSig + "\n" +
                       "      def : " + (res.Expected ?? "(no same-name member found)");

            if (!issueLines.TryGetValue(declAsm, out var list))
            {
                list = new List<string>();
                issueLines[declAsm] = list;
            }

            if (!list.Contains(line))
                list.Add(line);
        }

        Console.WriteLine("  resolved=" + mOk + "  MISMATCH=" + mMismatch + "  MISSING=" + mMissing);

        if (issueLines.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  >>> No member-reference incompatibilities found.");
        }
        else
        {
            foreach (var kv in issueLines.OrderBy(k => k.Key))
            {
                Console.WriteLine();
                Console.WriteLine("  ===== " + kv.Key + " (" + kv.Value.Count + ") =====");
                foreach (var l in kv.Value)
                    Console.WriteLine(l);
            }
        }

        Meta.DisposeAll();
    }

    private static MemberCheckResult CheckMember(MetadataReader declMd, TypeDefinitionHandle declTd, string name, bool isMethod, string strictSig, string looseSig)
    {
        var nameSeen = false;
        string looseExpected = null;
        var visited = new HashSet<string>();
        var cur = declTd;
        var cm = declMd;

        while (true)
        {
            if (cur.IsNil)
                break;

            var rowNo = MetadataTokens.GetRowNumber(cur);
            if (rowNo < 1 || rowNo > cm.TypeDefinitions.Count)
                break;

            var key = Meta.AsmOf(cm) + "!" + Meta.TypeFullName(cm, cm.GetTypeDefinition(cur));
            if (!visited.Add(key))
                break;

            var td = cm.GetTypeDefinition(cur);

            if (isMethod)
            {
                foreach (var mh in td.GetMethods())
                {
                    var m = cm.GetMethodDefinition(mh);
                    if (cm.GetString(m.Name) != name)
                        continue;

                    nameSeen = true;

                    if (MethodSig(m.DecodeSignature(TypeProv.WithAsm, null), true) == strictSig)
                        return new MemberCheckResult { Status = "OK" };

                    if (MethodSig(m.DecodeSignature(TypeProv.NoAsm, null), false) == looseSig)
                    {
                        // facade/assembly-qualification difference only (e.g. System.Runtime -> System.Private.CoreLib)
                        return new MemberCheckResult { Status = "OK" };
                    }

                    looseExpected = MethodSig(m.DecodeSignature(TypeProv.NoAsm, null), false);
                }
            }
            else
            {
                foreach (var fh in td.GetFields())
                {
                    var f = cm.GetFieldDefinition(fh);
                    if (cm.GetString(f.Name) != name)
                        continue;

                    nameSeen = true;

                    if (f.DecodeSignature(TypeProv.WithAsm, null) == strictSig)
                        return new MemberCheckResult { Status = "OK" };

                    looseExpected = f.DecodeSignature(TypeProv.NoAsm, null);

                    if (looseExpected == looseSig)
                        return new MemberCheckResult { Status = "OK" };
                }
            }

            var bt = td.BaseType;
            if (bt.IsNil)
                break;
            if (!Meta.TryResolveTypeHandle(cm, bt, out var bmd, out var btd))
                break;
            cm = bmd;
            cur = btd;
        }

        return new MemberCheckResult
        {
            Status = nameSeen ? "MISMATCH" : "MISSING",
            Expected = looseExpected
        };
    }
}

/// <summary>
/// Diffs the externally-visible API surface of two builds of one assembly and reports the
/// changes that break an already-compiled consumer.
/// </summary>
internal static class ApiDiff
{
    private sealed class Surface
    {
        public readonly Dictionary<string, string> Types = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Members = new Dictionary<string, string>();
    }

    public static void Run(string baselinePath, string headPath)
    {
        var baselineReader = Meta.Open(baselinePath);
        var headReader = Meta.Open(headPath);

        Console.WriteLine("### BASELINE : " + baselinePath);
        Console.WriteLine("### HEAD     : " + headPath);
        Console.WriteLine();

        var a = Dump(baselineReader);
        var b = Dump(headReader);

        Console.WriteLine("baseline: " + a.Types.Count + " public/protected types, " + a.Members.Count + " members");
        Console.WriteLine("head    : " + b.Types.Count + " public/protected types, " + b.Members.Count + " members");
        Console.WriteLine();

        var removedTypes = a.Types.Keys.Except(b.Types.Keys).OrderBy(x => x).ToList();
        var addedTypes = b.Types.Keys.Except(a.Types.Keys).OrderBy(x => x).ToList();
        var changedTypes = a.Types.Keys.Intersect(b.Types.Keys).Where(k => a.Types[k] != b.Types[k]).OrderBy(x => x).ToList();

        var removedMembers = a.Members.Keys.Except(b.Members.Keys).ToList();
        var addedMembers = b.Members.Keys.Except(a.Members.Keys).ToList();
        var changedMembers = a.Members.Keys.Intersect(b.Members.Keys).Where(k => a.Members[k] != b.Members[k]).OrderBy(x => x).ToList();

        Console.WriteLine("========== TYPES ==========");
        Console.WriteLine("  removed=" + removedTypes.Count + "  changed=" + changedTypes.Count + "  added=" + addedTypes.Count);

        foreach (var t in removedTypes)
            Console.WriteLine("  [REMOVED TYPE] " + t + "   (" + a.Types[t] + ")");

        foreach (var t in changedTypes)
            Console.WriteLine("  [CHANGED TYPE] " + t + "\n      was: " + a.Types[t] + "\n      now: " + b.Types[t]);

        Console.WriteLine();
        Console.WriteLine("  --- added types (non-breaking) ---");
        foreach (var t in addedTypes)
            Console.WriteLine("  [ADDED TYPE]  " + t + "   (" + b.Types[t] + ")");

        var addedByShape = new Dictionary<string, List<string>>();

        foreach (var k in addedMembers)
        {
            var key = ShapeKey(k);
            if (!addedByShape.TryGetValue(key, out var list))
            {
                list = new List<string>();
                addedByShape[key] = list;
            }
            list.Add(k);
        }

        var breaks = new List<string>();
        var sigChanges = new List<string>();

        foreach (var k in removedMembers.OrderBy(x => x))
        {
            if (addedByShape.TryGetValue(ShapeKey(k), out var candidates))
                sigChanges.Add("was: " + k + "\n      now: " + candidates[0]);
            else
                breaks.Add(k + "   (" + a.Members[k] + ")");
        }

        Console.WriteLine();
        Console.WriteLine("========== MEMBERS ==========");
        Console.WriteLine("  removed=" + removedMembers.Count + "  changed=" + changedMembers.Count + "  added=" + addedMembers.Count);

        foreach (var line in breaks)
            Console.WriteLine("  [REMOVED]    " + line);

        foreach (var line in sigChanges)
            Console.WriteLine("  [SIG CHANGE] " + line);

        foreach (var k in changedMembers.OrderBy(x => x))
            Console.WriteLine("  [CHANGED]    " + k + "\n      was: " + a.Members[k] + "\n      now: " + b.Members[k]);

        Console.WriteLine();
        Console.WriteLine("========== SUMMARY ==========");
        Console.WriteLine("  binary-breaking type removals   : " + removedTypes.Count);
        Console.WriteLine("  binary-breaking member removals : " + breaks.Count);
        Console.WriteLine("  binary-breaking sig changes     : " + sigChanges.Count);
        Console.WriteLine("  non-breaking type additions     : " + addedTypes.Count);
        Console.WriteLine("  non-breaking member additions   : " + addedMembers.Count);

        Meta.DisposeAll();
    }

    private static string ShapeKey(string memberKey)
    {
        var parts = memberKey.Split('|');
        if (parts.Length < 4)
            return memberKey;

        var sig = parts[3];
        var open = sig.IndexOf('(');
        var close = sig.LastIndexOf(')');
        var paramCount = 0;

        if (open >= 0 && close > open + 1)
            paramCount = SplitTopLevel(sig.Substring(open + 1, close - open - 1)).Count;

        return parts[0] + "|" + parts[1] + "|" + parts[2] + "|" + paramCount;
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        if (string.IsNullOrEmpty(s))
            return parts;

        var depth = 0;
        var start = 0;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '<' || c == '[')
                depth++;
            else if (c == '>' || c == ']')
                depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }

        parts.Add(s.Substring(start).Trim());
        return parts;
    }

    private static Surface Dump(MetadataReader md)
    {
        var surf = new Surface();

        foreach (var tdh in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdh);
            if (!IsVisibleType(td))
                continue;

            var fullName = Meta.TypeFullName(md, td) + (td.GetGenericParameters().Count > 0 ? "`" + td.GetGenericParameters().Count : "");
            var kind = TypeKind(md, td);
            var visibility = TypeVisibility(td);

            var baseType = td.BaseType.IsNil ? "" : " base=" + Meta.EntityTypeName(md, td.BaseType);
            var attrs = "";

            if ((td.Attributes & TypeAttributes.Abstract) != 0 && kind == "class")
                attrs += " abstract";
            if ((td.Attributes & TypeAttributes.Sealed) != 0)
                attrs += " sealed";

            surf.Types[fullName] = visibility + " " + kind + attrs + baseType;

            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (!IsVisibleMethod(m))
                    continue;

                var name = md.GetString(m.Name);
                var dec = m.DecodeSignature(TypeProv.WithAsm, null);
                var sig = "(" + string.Join(",", dec.ParameterTypes) + ")->" + dec.ReturnType;
                var extra = MethodVisibility(m);

                if ((m.Attributes & MethodAttributes.Static) != 0)
                    extra += " static";
                if ((m.Attributes & MethodAttributes.Abstract) != 0)
                    extra += " abstract";
                else if ((m.Attributes & MethodAttributes.Virtual) != 0)
                    extra += " virtual";

                surf.Members[fullName + "|M|" + name + (dec.GenericParameterCount > 0 ? "`" + dec.GenericParameterCount : "") + "|" + sig] = extra;
            }

            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if (!IsVisibleField(f))
                    continue;

                var extra = FieldVisibility(f);

                if ((f.Attributes & FieldAttributes.Static) != 0)
                    extra += " static";
                if ((f.Attributes & FieldAttributes.Literal) != 0)
                    extra += " const";

                surf.Members[fullName + "|F|" + md.GetString(f.Name) + "|" + f.DecodeSignature(TypeProv.WithAsm, null)] = extra;
            }

            foreach (var ph in td.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var name = md.GetString(p.Name);

                string propVisibility = null;
                var accessors = p.GetAccessors();

                foreach (var ah in new[] { accessors.Getter, accessors.Setter })
                {
                    if (ah.IsNil)
                        continue;
                    var am = md.GetMethodDefinition(ah);
                    if (!IsVisibleMethod(am))
                        continue;
                    propVisibility ??= MethodVisibility(am);
                }

                if (propVisibility == null)
                    continue;

                surf.Members[fullName + "|P|" + name + "|" + p.DecodeSignature(TypeProv.WithAsm, null).ReturnType] = propVisibility;
            }

            foreach (var eh in td.GetEvents())
            {
                var ev = md.GetEventDefinition(eh);
                var acc = ev.GetAccessors();

                if (acc.Adder.IsNil && acc.Remover.IsNil)
                    continue;

                var eventVisibility = (string)null;

                foreach (var ah in new[] { acc.Adder, acc.Remover })
                {
                    if (ah.IsNil)
                        continue;
                    var am = md.GetMethodDefinition(ah);
                    if (IsVisibleMethod(am))
                        eventVisibility ??= MethodVisibility(am);
                }

                if (eventVisibility == null)
                    continue;

                var evType = ev.Type.IsNil ? "?" : Meta.EntityTypeName(md, ev.Type);
                surf.Members[fullName + "|E|" + md.GetString(ev.Name) + "|" + evType] = eventVisibility;
            }
        }

        return surf;
    }

    private static bool IsVisibleType(TypeDefinition td)
    {
        switch (td.Attributes & TypeAttributes.VisibilityMask)
        {
            case TypeAttributes.Public:
            case TypeAttributes.NestedPublic:
            case TypeAttributes.NestedFamily:
            case TypeAttributes.NestedFamORAssem:
                return true;

            default:
                return false;
        }
    }

    private static string TypeVisibility(TypeDefinition td)
    {
        switch (td.Attributes & TypeAttributes.VisibilityMask)
        {
            case TypeAttributes.Public:
            case TypeAttributes.NestedPublic:
                return "public";

            case TypeAttributes.NestedFamily:
                return "protected";

            case TypeAttributes.NestedFamORAssem:
                return "protected internal";

            default:
                return "internal";
        }
    }

    private static string TypeKind(MetadataReader md, TypeDefinition td)
    {
        if ((td.Attributes & TypeAttributes.Interface) != 0)
            return "interface";

        var baseName = td.BaseType.IsNil ? null : Meta.EntityTypeName(md, td.BaseType);

        if (baseName == "System.Enum")
            return "enum";
        if (baseName == "System.ValueType")
            return "struct";
        if (baseName == "System.MulticastDelegate")
            return "delegate";

        return "class";
    }

    private static bool IsVisibleMethod(MethodDefinition m)
    {
        switch (m.Attributes & MethodAttributes.MemberAccessMask)
        {
            case MethodAttributes.Public:
            case MethodAttributes.Family:
            case MethodAttributes.FamORAssem:
                return true;

            default:
                return false;
        }
    }

    private static string MethodVisibility(MethodDefinition m)
    {
        switch (m.Attributes & MethodAttributes.MemberAccessMask)
        {
            case MethodAttributes.Public:
                return "public";

            case MethodAttributes.Family:
                return "protected";

            case MethodAttributes.FamORAssem:
                return "protected internal";

            default:
                return "other";
        }
    }

    private static bool IsVisibleField(FieldDefinition f)
    {
        switch (f.Attributes & FieldAttributes.FieldAccessMask)
        {
            case FieldAttributes.Public:
            case FieldAttributes.Family:
            case FieldAttributes.FamORAssem:
                return true;

            default:
                return false;
        }
    }

    private static string FieldVisibility(FieldDefinition f)
    {
        switch (f.Attributes & FieldAttributes.FieldAccessMask)
        {
            case FieldAttributes.Public:
                return "public";

            case FieldAttributes.Family:
                return "protected";

            case FieldAttributes.FamORAssem:
                return "protected internal";

            default:
                return "other";
        }
    }
}
