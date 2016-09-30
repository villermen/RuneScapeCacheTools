﻿using System;
using System.IO;
using RuneScapeCacheToolsTests.Fixtures;
using Villermen.RuneScapeCacheTools.Cache;
using Villermen.RuneScapeCacheTools.Cache.RuneTek5;
using Xunit;
using Xunit.Abstractions;

namespace RuneScapeCacheToolsTests
{
    [Collection("TestCache")]
    public class RuneTek5CacheTests
    {
        private ITestOutputHelper Output { get; }

        private CacheFixture Fixture { get; }

        public RuneTek5CacheTests(ITestOutputHelper output, CacheFixture fixture)
        {
            Output = output;
            Fixture = fixture;
        }

        /// <summary>
        /// Test for a file that exists, an archive file that exists and a file that doesn't exist.
        /// </summary>
        [Fact]
        public void TestGetFile()
        {
            var file = Fixture.RuneTek5Cache.GetFile(12, 3);

            var fileData = file.Data;

            Assert.True(fileData.Length > 0, "File's data is empty.");

            var archiveFile = Fixture.RuneTek5Cache.GetFile(17, 5);

            var archiveEntry = archiveFile.Entries[255];

            Assert.True(archiveEntry.Length > 0, "Archive entry's data is empty.");

            try
            {
                Fixture.RuneTek5Cache.GetFile(40, 30);

                Assert.True(false, "Cache returned a file that shouldn't exist.");
            }
            catch (CacheException exception)
            {
                Assert.True(exception.Message.Contains("incomplete"), "Non-existent file cache exception had the wrong message.");
            }
        }

        [Theory]
        [InlineData(40)]
        [InlineData(17)]
        [InlineData(12)]
        public void TestGetReferenceTable(int indexId)
        {
            var referenceTable = Fixture.RuneTek5Cache.GetReferenceTable(indexId);
        }

        [Theory]
        [InlineData(12, 3)]
        public void TestExtract(int indexId, int fileId)
        {
            var expectedFilePath = $"output/extracted/{indexId}/{fileId}";

            var startTime = DateTime.UtcNow - TimeSpan.FromSeconds(1);

            Fixture.RuneTek5Cache.Extract(indexId, fileId, true);

            Assert.True(File.Exists(expectedFilePath), $"File was not extracted, or not extracted to {expectedFilePath}.");

            var modifiedTime = File.GetLastAccessTimeUtc(expectedFilePath);

            Assert.True(startTime <= modifiedTime, $"Starting time of test ({startTime}) was not earlier or equal to extracted file modified time ({modifiedTime}).");
        }

        [Fact]
        public void TestExtractWithEntries()
        {
            var expectedFilePath = "output/extracted/17/5-65";

            var startTime = DateTime.UtcNow - TimeSpan.FromSeconds(1);

            Fixture.RuneTek5Cache.Extract(17, 5, true);

            Assert.True(File.Exists(expectedFilePath), $"File entry was not extracted, or not extracted to {expectedFilePath}.");

            var modifiedTime = File.GetLastAccessTimeUtc(expectedFilePath);

            Assert.True(startTime <= modifiedTime, $"Starting time of test ({startTime}) was not earlier or equal to extracted file modified time ({modifiedTime}).");
        }

        // TODO: Test file extension naming
    }
}