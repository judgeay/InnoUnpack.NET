using InnoUnpack.NET;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.Tests;

/// <summary>
///     新增 API 测试：单文件提取/打开（同步/异步）、异步枚举、时间戳、文件属性与权限。
/// </summary>
public class ApiTests {
	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void FindFileAndOpenByPath(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var first = archive.EnumerateFiles().First();

		var found = archive.FindFile(first.Path);
		Assert.NotNull(found);
		Assert.Equal(first.Path, found!.Path);

		// 按路径打开的解压流应能完整读出文件内容
		using var stream = archive.OpenFile(first.Path);
		using var ms = new MemoryStream();
		stream.CopyTo(ms);
		Assert.Equal((long)first.Size, ms.Length);
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-5.5.9-unicode.exe")]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	[InlineData("innosetup-7.0.2-x64.exe")]
	public void OpenFileSequentialReadsMatchChecksums(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		List<InnoArchiveFile> files = [.. archive.EnumerateFiles()];

		// 顺序（复用缓存解码器续读）、逆序（每次回退重开 chunk）与交错部分读取均应得到正确内容
		foreach (var file in files) {
			AssertContentMatchesChecksum(archive, file);
		}

		for (var i = files.Count - 1; i >= 0; i--) {
			AssertContentMatchesChecksum(archive, files[i]);
		}

		for (var i = 0; i < files.Count; i++) {
			if (i % 2 == 0) {
				// 只读取部分数据即释放：下一次打开应从正确位置续读
				using var partial = archive.OpenFile(files[i]);
				partial.ReadExactly(new byte[(int)Math.Min(files[i].Size, 100)]);
			} else {
				AssertContentMatchesChecksum(archive, files[i]);
			}
		}
	}

	[Fact]
	public void OpenFileAfterExtractionReadsCorrectData() {
		Fixtures.SkipIfMissing("innosetup-6.7.3.exe");

		using var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-6.7.3.exe"));
		List<InnoArchiveFile> files = [.. archive.EnumerateFiles()];
		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "openfile-after-extract");
		try {
			// 缓存解码器之后的提取会移动共享切片读取器位置，缓存应失效而非读出错误数据
			AssertContentMatchesChecksum(archive, files[0]);
			archive.ExtractFile(files[^1].Path, outputDir);
			AssertContentMatchesChecksum(archive, files[1]);
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Fact]
	public void OpenFileAfterDisposeThrows() {
		Fixtures.SkipIfMissing("innosetup-6.7.3.exe");

		var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-6.7.3.exe"));
		var file = archive.EnumerateFiles().First();
		var stream = archive.OpenFile(file);
		archive.Dispose();
		stream.Dispose(); // archive 释放后归还解码器不应抛出
		Assert.Throws<ObjectDisposedException>(() => archive.OpenFile(file));
	}

	static private void AssertContentMatchesChecksum(InnoSetupArchive archive, InnoArchiveFile file) {
		using var stream = archive.OpenFile(file);
		using var hasher = FileHasher.Create(file.DataEntry.Checksum.Type);
		var buffer = new byte[81920];
		long total = 0;
		int n;
		while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) {
			hasher?.Update(buffer.AsSpan(0, n));
			total += n;
		}

		Assert.Equal((long)file.Size, total);
		Assert.True(hasher is null || hasher.Verify(file.DataEntry.Checksum), $"校验和不匹配：{file.Path}");
	}

	[Fact]
	public void FindFileReturnsNullForMissingPath() {
		Fixtures.SkipIfMissing("innosetup-5.6.1-unicode.exe");
		using var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-5.6.1-unicode.exe"));
		Assert.Null(archive.FindFile("不存在的路径/foo.bin"));
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void ExtractFileWritesSingleFile(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var target = archive.EnumerateFiles().OrderByDescending(f => f.Size).First();
		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "extract-file-" + fixture[..^4]);
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			archive.ExtractFile(target.Path, outputDir);
			var files = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories).ToList();
			Assert.Single(files);
			Assert.Equal((long)target.Size, new FileInfo(files[0]).Length);
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Fact]
	public async Task EnumerateFilesAsyncMatchesSync() {
		Fixtures.SkipIfMissing("innosetup-5.6.1-unicode.exe");
		using var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-5.6.1-unicode.exe"));

		var sync = archive.EnumerateFiles().Select(f => f.Path).ToList();
		var async = (await archive.EnumerateFilesAsync()).Select(f => f.Path).ToList();
		Assert.Equal(sync, async);
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public async Task ExtractFileAsyncMatchesSync(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var target = archive.EnumerateFiles().OrderByDescending(f => f.Size).First();

		var syncDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "extract-file-sync-" + fixture[..^4]);
		var asyncDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "extract-file-async-" + fixture[..^4]);
		if (Directory.Exists(syncDir)) {
			Directory.Delete(syncDir, true);
		}

		if (Directory.Exists(asyncDir)) {
			Directory.Delete(asyncDir, true);
		}

		try {
			archive.ExtractFile(target.Path, syncDir);
			await archive.ExtractFileAsync(target.Path, asyncDir);

			var syncFiles = Directory.EnumerateFiles(syncDir, "*", SearchOption.AllDirectories).ToList();
			var asyncFiles = Directory.EnumerateFiles(asyncDir, "*", SearchOption.AllDirectories).ToList();
			Assert.Single(syncFiles);
			Assert.Single(asyncFiles);
			Assert.True(File.ReadAllBytes(syncFiles[0]).AsSpan().SequenceEqual(File.ReadAllBytes(asyncFiles[0])));
		} finally {
			if (Directory.Exists(syncDir)) {
				Directory.Delete(syncDir, true);
			}

			if (Directory.Exists(asyncDir)) {
				Directory.Delete(asyncDir, true);
			}
		}
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public async Task OpenFileAsyncMatchesSync(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var first = archive.EnumerateFiles().First();

		using var syncStream = archive.OpenFile(first.Path);
		using var asyncStream = await archive.OpenFileAsync(first.Path);
		using var syncMs = new MemoryStream();
		using var asyncMs = new MemoryStream();
		syncStream.CopyTo(syncMs);
		asyncStream.CopyTo(asyncMs);
		Assert.True(syncMs.ToArray().AsSpan().SequenceEqual(asyncMs.ToArray()));
	}

	[Fact]
	public void ArchiveFileExposesAttributesAndPermission() {
		Fixtures.SkipIfMissing("innosetup-5.6.1-unicode.exe");
		using var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-5.6.1-unicode.exe"));

		var file = archive.EnumerateFiles().First();
		// 新字段与底层条目一致
		Assert.Equal(file.Entry.Attributes, file.Attributes);
		Assert.Equal(file.Entry.Permission, file.Permission);
		Assert.Equal(file.DataEntry.Timestamp, file.Timestamp);
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-5.5.9-unicode.exe")]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	[InlineData("innosetup-7.0.2-x64.exe")]
	public void TimestampsArePlausible(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var file = archive.EnumerateFiles().First(f => f.Timestamp != default);

		// 回归：曾因 FILETIME→ticks 换算错误（多除 10）得到约 1974 年；
		// 官方安装包时间戳应在 2000 年之后
		Assert.True(file.Timestamp.Year >= 2000, $"时间戳异常：{file.Timestamp:yyyy-MM-dd}");
	}

	[Fact]
	public void ApplyFileAttributesSetsUnixPermissions() {
		// 官方安装包文件权限多为未指定，使用合成条目验证权限应用逻辑
		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "file-attrs");
		Directory.CreateDirectory(outputDir);
		var target = Path.Combine(outputDir, "script.sh");
		File.WriteAllText(target, "#!/bin/sh\n");
		try {
			InnoArchiveFile file = new() { Permission = 493 }; // 0o755
			InnoSetupArchive.ApplyFileAttributes(target, file);

			if (!OperatingSystem.IsWindows()) {
				Assert.Equal((UnixFileMode)493, File.GetUnixFileMode(target));
			}
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}
}
