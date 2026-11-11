// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using osu.Framework.IO.Stores;
using Veldrid;
using Veldrid.SPIRV;

namespace osu.Framework.Tests.Graphics
{
    [TestFixture]
    public class VertexMaskShaderTest
    {
        private static readonly Regex uniform_pattern = new Regex(@"^(\s*layout\s*\(.*)set\s*=\s*(-?\d)(.*\)\s*(?:(?:readonly\s*)?buffer|uniform))", RegexOptions.Multiline);
        private static readonly Regex include_pattern = new Regex(@"^\s*#\s*include\s+[""<](.*)["">]");
        private static readonly Regex output_pattern = new Regex(@"^\s*layout\s*\(\s*location\s*=\s*(-?\d+)\s*\)\s*out\s+((?:(?:lowp|mediump|highp)\s+)?\w+)\s+(\w+)\s*;", RegexOptions.Multiline);
        private static readonly Regex input_pattern = new Regex(@"^\s*layout\s*\(\s*location\s*=\s*(-?\d+)\s*\)\s*in\s+((?:(?:lowp|mediump|highp)\s+)?\w+)\s+(\w+)\s*;", RegexOptions.Multiline);

        [TestCase("sh_Texture.fs")]
        [TestCase("sh_Blur.fs")]
        [TestCase("sh_LiquidGlass.fs")]
        public void TestTextureShaderLinksWithMaskTable(string fragment)
        {
            var resources = new NamespacedResourceStore<byte[]>(
                new NamespacedResourceStore<byte[]>(new DllResourceStore(typeof(Game).Assembly), @"Resources"),
                "Shaders");

            string vertex = preprocess(resources, "sh_Texture2D.vs", true);
            string fragmentText = passThroughVertexOutputs(preprocess(resources, fragment, false), vertex);

            byte[] vertexSpirv = SpirvCompilation.CompileGlslToSpirv(vertex, null, ShaderStages.Vertex, new GlslCompileOptions(true)).SpirvBytes;
            byte[] fragmentSpirv = SpirvCompilation.CompileGlslToSpirv(fragmentText, null, ShaderStages.Fragment, new GlslCompileOptions(true)).SpirvBytes;
            VertexFragmentCompilationResult linked = SpirvCompilation.CompileVertexFragment(vertexSpirv, fragmentSpirv, CrossCompileTarget.HLSL, new CrossCompileOptions());

            Assert.That(linked.Reflection.ResourceLayouts.SelectMany(layout => layout.Elements).Select(element => element.Name), Does.Contain("g_VertexMasks"));
            Assert.That(linked.VertexShader, Does.Contain("g_VertexMaskData"));
            Assert.That(linked.VertexShader, Does.Contain("[640]"));
            Assert.That(linked.VertexShader, Does.Contain(" * 5"));

            if (fragmentText.Contains("getRoundedColor", StringComparison.Ordinal))
            {
                Assert.That(linked.FragmentShader, Does.Contain("nointerpolation float4 v_MaskingRect"));
                Assert.That(linked.FragmentShader, Does.Contain("nointerpolation float4 v_MaskParameters"));
            }

            Assert.That(fragmentText, Does.Not.Contain("{{ "));
        }

        private static string preprocess(NamespacedResourceStore<byte[]> resources, string name, bool vertex)
        {
            string code = load(resources, name, true);
            int minSet = uniform_pattern.Matches(code)
                                        .Where(match => match.Success)
                                        .Select(match => int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
                                        .DefaultIfEmpty(0)
                                        .Min();

            code = uniform_pattern.Replace(code, match =>
                $"{match.Groups[1].Value}set = {int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + Math.Abs(minSet)}{match.Groups[3].Value}");

            string outputHeader = vertex ? "Internal/sh_Vertex_Output.h" : "Internal/sh_Fragment_Output.h";
            string output = load(resources, outputHeader, false).Replace("{{ real_main }}", "__internal_real_main");
            return Regex.Replace(code, @"void main\((.*)\)", "void __internal_real_main()") + output + "\n";
        }

        private static string passThroughVertexOutputs(string fragment, string vertex)
        {
            var inputs = input_pattern.Matches(fragment).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
            int location = output_pattern.Matches(fragment).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max() + 1;
            var layout = new StringBuilder();
            var assignment = new StringBuilder();

            foreach (Match output in output_pattern.Matches(vertex))
            {
                int source = int.Parse(output.Groups[1].Value, CultureInfo.InvariantCulture);

                if (inputs.Contains(source))
                    continue;

                string inputName = $"__unused_input_{source}";
                string outputName = $"__unused_output_{location}";
                layout.AppendLine($"layout (location = {source}) in {output.Groups[2].Value} {inputName};");
                layout.AppendLine($"layout (location = {location}) out {output.Groups[2].Value} {outputName};");
                assignment.AppendLine($"{outputName} = {inputName};");
                location++;
            }

            return fragment.Replace("{{ fragment_output_layout }}", layout.ToString())
                           .Replace("{{ fragment_output_assignment }}", assignment.ToString());
        }

        private static string load(NamespacedResourceStore<byte[]> resources, string name, bool mainFile)
        {
            var preamble = new StringBuilder();
            var body = new StringBuilder();
            loadInto(resources, name, mainFile, preamble, body);
            return preamble + body.ToString();
        }

        private static void loadInto(NamespacedResourceStore<byte[]> resources, string name, bool mainFile, StringBuilder preamble, StringBuilder body)
        {
            byte[]? bytes = resources.Get(name);
            Assert.That(bytes, Is.Not.Null, name);

            using var reader = new StreamReader(new MemoryStream(bytes!));

            if (mainFile)
            {
                loadInto(resources, "Internal/sh_Compatibility.h", false, preamble, body);
                loadInto(resources, "Internal/sh_GlobalUniforms.h", false, preamble, body);
            }

            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("#version", StringComparison.Ordinal) || line.StartsWith("#extension", StringComparison.Ordinal))
                {
                    preamble.AppendLine(line);
                    continue;
                }

                Match include = include_pattern.Match(line);

                if (include.Success)
                    loadInto(resources, include.Groups[1].Value.Trim(), false, preamble, body);
                else
                    body.AppendLine(line);
            }
        }
    }
}
