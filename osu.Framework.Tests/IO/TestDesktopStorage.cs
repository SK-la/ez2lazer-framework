// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
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
            string scope = Guid.NewGuid().ToString();
            string scopePath = Path.Combine(TestRunHeadlessGameHost.TemporaryTestDirectory, scope);

            try
            {
                using (var storage = new TemporaryNativeStorage(Path.Combine(scope, "osu")))
                {
                    string basePath = storage.GetFullPath(string.Empty);
                    string siblingDirectory = basePath + "-lazer";
                    string siblingFile = Path.Combine(siblingDirectory, "client.realm");

                    Assert.That(basePath, Does.EndWith(Path.DirectorySeparatorChar + "osu"));

                    Directory.CreateDirectory(siblingDirectory);
                    File.WriteAllText(siblingFile, "secret");

                    Assert.Throws<ArgumentException>(() => storage.GetFullPath("../osu-lazer"));
                    Assert.Throws<ArgumentException>(() => storage.GetFullPath("../osu-lazer/client.realm"));
                    Assert.Throws<ArgumentException>(() => storage.GetFullPath("../osu!"));
                    Assert.Throws<ArgumentException>(() => storage.GetFullPath("../OSU-lazer/client.realm"));
                    Assert.Throws<ArgumentException>(() => storage.GetFullPath(siblingFile));
                    Assert.Throws<ArgumentException>(() => storage.GetStorageForDirectory("../osu-lazer"));
                    Assert.Throws<ArgumentException>(() => storage.Delete("../osu-lazer/client.realm"));
                    Assert.Throws<ArgumentException>(() => storage.DeleteDirectory("../osu-lazer"));
                    Assert.Throws<ArgumentException>(() =>
                    {
                        using var stream = storage.GetStream("../osu-lazer/client.realm");
                    });
                    Assert.Throws<ArgumentException>(() =>
                    {
                        using var stream = storage.GetStream("../osu-lazer/client.realm", FileAccess.Write);
                    });
                    Assert.Throws<ArgumentException>(() =>
                    {
                        using var stream = storage.CreateFileSafely("../osu-lazer/client.realm");
                    });

                    Assert.That(File.ReadAllText(siblingFile), Is.EqualTo("secret"));
                    Assert.That(storage.GetFullPath(string.Empty), Is.EqualTo(basePath));
                    Assert.That(storage.GetFullPath("../osu"), Is.EqualTo(basePath));
                    Assert.That(storage.GetFullPath("../osu/"), Is.EqualTo(basePath + Path.DirectorySeparatorChar));
                    Assert.That(storage.GetFullPath("../osu/skins/file"), Is.EqualTo(Path.Combine(basePath, "skins", "file")));
                    Assert.That(storage.GetFullPath("osu-lazer/client.realm"), Is.EqualTo(Path.Combine(basePath, "osu-lazer", "client.realm")));
                    Assert.That(storage.GetFullPath(Path.Combine(basePath, "skins", "file")), Is.EqualTo(Path.Combine(basePath, "skins", "file")));
                    Assert.That(storage.GetStorageForDirectory("../osu").GetFullPath(string.Empty), Is.EqualTo(basePath));
                }
            }
            finally
            {
                if (Directory.Exists(scopePath))
                    Directory.Delete(scopePath, true);
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
