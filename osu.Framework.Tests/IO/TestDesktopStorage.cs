// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
using osu.Framework.Platform;
using osu.Framework.Testing;

// ReSharper disable AccessToDisposedClosure

namespace osu.Framework.Tests.IO
{
    [TestFixture]
    public class TestDesktopStorage
    {
        [Test]
        public void TestRelativePaths()
        {
            string guid = Guid.NewGuid().ToString();

            using (var storage = new TemporaryNativeStorage(guid))
            {
                string basePath = storage.GetFullPath(string.Empty);

                Assert.IsTrue(basePath.EndsWith(guid, StringComparison.Ordinal));

                Assert.Throws<ArgumentException>(() => storage.GetFullPath("../"));
                Assert.Throws<ArgumentException>(() => storage.GetFullPath(".."));
                Assert.Throws<ArgumentException>(() => storage.GetFullPath("./../"));

                Assert.AreEqual(Path.GetFullPath(Path.Combine(basePath, "sub", "test")) + Path.DirectorySeparatorChar, storage.GetFullPath("sub/test/"));
                Assert.AreEqual(Path.GetFullPath(Path.Combine(basePath, "sub", "test")), storage.GetFullPath("sub/test"));
            }
        }

        [Test]
        public void TestAttemptEscapeRoot()
        {
            string guid = Guid.NewGuid().ToString();

            using (var storage = new TemporaryNativeStorage(guid))
            {
                Assert.Throws<ArgumentException>(() =>
                {
                    using var x = storage.GetStream("../test");
                });

                Assert.Throws<ArgumentException>(() => storage.GetStorageForDirectory("../"));
            }
        }

        [Test]
        public void TestPrefixSiblingEscape()
        {
            string parent = Path.Combine(TestRunHeadlessGameHost.TemporaryTestDirectory, "sandbox-" + Guid.NewGuid().ToString("N"));
            var storage = new NativeStorage(Path.Combine(parent, "osu"));

            try
            {
                storage.GetFullPath("./", true);

                string root = storage.GetFullPath(string.Empty);
                string siblingDirectory = root + "-lazer";
                string siblingFile = Path.Combine(siblingDirectory, "client.realm");

                Assert.Throws<ArgumentException>(() => storage.GetFullPath("../osu-lazer"));
                Assert.Throws<ArgumentException>(() => storage.GetFullPath("../osu-lazer/client.realm"));
                Assert.Throws<ArgumentException>(() => storage.GetFullPath("../osu!"));
                Assert.Throws<ArgumentException>(() => storage.GetFullPath(siblingFile));
                Assert.Throws<ArgumentException>(() => storage.GetStorageForDirectory("../osu-lazer"));
                Assert.That(Directory.Exists(siblingDirectory), Is.False);

                Directory.CreateDirectory(siblingDirectory);
                File.WriteAllText(siblingFile, "secret");

                Assert.Throws<ArgumentException>(() =>
                {
                    using var x = storage.GetStream("../osu-lazer/client.realm");
                });
                Assert.Throws<ArgumentException>(() => storage.Delete("../osu-lazer/client.realm"));
                Assert.Throws<ArgumentException>(() => storage.DeleteDirectory("../osu-lazer"));
                Assert.That(File.ReadAllText(siblingFile), Is.EqualTo("secret"));

                using (var stream = storage.CreateFileSafely("inside.txt"))
                    stream.WriteByte(1);

                Assert.That(storage.GetFiles(string.Empty), Is.EquivalentTo(new[] { "inside.txt" }));
                Assert.AreEqual(root, storage.GetFullPath("../osu"));
                Assert.AreEqual(Path.Combine(root, "osu-lazer", "file"), storage.GetFullPath("osu-lazer/file"));
                Assert.AreEqual(Path.Combine(root, "skins"), storage.GetFullPath("../osu/skins"));
            }
            finally
            {
                if (Directory.Exists(parent))
                    Directory.Delete(parent, true);
            }
        }

        [Test]
        public void TestGetSubDirectoryStorage()
        {
            string guid = Guid.NewGuid().ToString();

            using (var storage = new TemporaryNativeStorage(guid))
            {
                Assert.That(storage.GetStorageForDirectory("subdir").GetFullPath(string.Empty), Is.EqualTo(Path.Combine(storage.GetFullPath(string.Empty), "subdir")));
            }
        }

        [Test]
        public void TestGetEmptySubDirectoryStorage()
        {
            string guid = Guid.NewGuid().ToString();

            using (var storage = new TemporaryNativeStorage(guid))
            {
                Assert.That(storage.GetStorageForDirectory(string.Empty).GetFullPath(string.Empty), Is.EqualTo(storage.GetFullPath(string.Empty)));
            }
        }

        [Test]
        public void TestCaseSensitivity()
        {
            bool isCaseSensitive = isCaseSensitiveFilesystem();

            // Then check our methods match this.
            using (var storage = new TemporaryNativeStorage("test"))
            {
                using (var stream = storage.CreateFileSafely("test-file"))
                    stream.WriteByte(0);

                Assert.That(storage.Exists("test-file"), Is.True);
                Assert.That(storage.Exists("TEST-FILE"), Is.EqualTo(!isCaseSensitive));

                using (var storage2 = new TemporaryNativeStorage("TEST"))
                {
                    Assert.That(storage2.Exists("test-file"), Is.EqualTo(!isCaseSensitive));
                    Assert.That(storage2.Exists("TEST-FILE"), Is.EqualTo(!isCaseSensitive));

                    // Test that base path lookup checks don't incorrectly fail.
                    // This should pass regardless of case sensitivity of the underlying partition.
                    var nested = storage2.GetStorageForDirectory("subdir");

                    using (var stream = nested.CreateFileSafely("test-file"))
                        stream.WriteByte(0);

                    Assert.That(nested.GetDirectories(string.Empty), Is.Empty);
                    Assert.That(nested.GetFiles(string.Empty), Has.One.Items);
                }
            }
        }

        private static bool isCaseSensitiveFilesystem()
        {
            // First test if this unit test is running on a filesystem which is case sensitive.
            string testFile = Path.Combine(TestRunHeadlessGameHost.TemporaryTestDirectory, "case-sensitivity-test");
            File.WriteAllText(testFile, "test");
            bool isCaseSensitive = !File.Exists(testFile.ToUpperInvariant());
            File.Delete(testFile);
            return isCaseSensitive;
        }
    }
}
