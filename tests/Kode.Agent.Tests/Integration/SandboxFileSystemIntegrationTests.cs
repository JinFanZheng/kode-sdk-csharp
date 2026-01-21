using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Tests.Helpers;
using Xunit;

namespace Kode.Agent.Tests.Integration;

/// <summary>
/// Integration tests for sandbox file system operations.
/// </summary>
public class SandboxFileSystemIntegrationTests : IAsyncDisposable
{
    private readonly LocalSandbox _sandbox;
    private readonly string _testDir;

    public SandboxFileSystemIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"sandbox_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        
        _sandbox = new LocalSandbox(new SandboxOptions
        {
            WorkingDirectory = _testDir
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _sandbox.DisposeAsync();
        
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteFileLifecycle_CreateReadUpdateDelete()
    {
        // Arrange
        var fileName = "lifecycle_test.txt";
        var initialContent = "Initial content";
        var updatedContent = "Updated content";

        // Act & Assert - Create
        await _sandbox.WriteFileAsync(fileName, initialContent);
        Assert.True(await _sandbox.FileExistsAsync(fileName));

        // Read
        var readContent = await _sandbox.ReadFileAsync(fileName);
        Assert.Equal(initialContent, readContent);

        // Update
        await _sandbox.WriteFileAsync(fileName, updatedContent);
        var updatedRead = await _sandbox.ReadFileAsync(fileName);
        Assert.Equal(updatedContent, updatedRead);

        // Delete
        await _sandbox.DeleteFileAsync(fileName);
        Assert.False(await _sandbox.FileExistsAsync(fileName));
    }

    [Fact]
    public async Task DirectoryOperations_CreateListDelete()
    {
        // Arrange
        var dirName = "test_directory";
        var subDir = Path.Combine(dirName, "subdir");
        var file1 = Path.Combine(dirName, "file1.txt");
        var file2 = Path.Combine(dirName, "file2.txt");

        // Act - Create directory structure
        await _sandbox.CreateDirectoryAsync(subDir);
        await _sandbox.WriteFileAsync(file1, "content1");
        await _sandbox.WriteFileAsync(file2, "content2");

        // Assert - List directory
        var entries = await _sandbox.ListDirectoryAsync(dirName);
        Assert.Equal(3, entries.Count); // subdir, file1.txt, file2.txt
        
        var fileNames = entries.Select(e => e.Name).ToList();
        Assert.Contains("subdir", fileNames);
        Assert.Contains("file1.txt", fileNames);
        Assert.Contains("file2.txt", fileNames);

        // Cleanup
        await _sandbox.DeleteFileAsync(file1);
        await _sandbox.DeleteFileAsync(file2);
        await _sandbox.DeleteDirectoryAsync(subDir);
        await _sandbox.DeleteDirectoryAsync(dirName);
    }

    [UnixOnlyFact]
    public async Task CommandExecution_WithWorkingDirectory()
    {
        // Arrange
        var subDir = "cmd_test_dir";
        await _sandbox.CreateDirectoryAsync(subDir);
        await _sandbox.WriteFileAsync(Path.Combine(subDir, "marker.txt"), "exists");

        // Act - Execute command in subdirectory (Unix-specific: ls -la)
        var result = await _sandbox.ExecuteCommandAsync("ls -la", new CommandOptions
        {
            WorkingDirectory = Path.Combine(_testDir, subDir)
        });

        // Assert
        Assert.True(result.Success);
        Assert.Contains("marker.txt", result.Stdout);

        // Cleanup
        await _sandbox.DeleteFileAsync(Path.Combine(subDir, "marker.txt"));
        await _sandbox.DeleteDirectoryAsync(subDir);
    }

    [UnixOnlyFact]
    public async Task CommandExecution_WithEnvironmentVariables()
    {
        // Arrange & Act (Unix-specific: $VAR syntax)
        var result = await _sandbox.ExecuteCommandAsync("echo $TEST_VAR", new CommandOptions
        {
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["TEST_VAR"] = "HelloFromEnv"
            }
        });

        // Assert
        Assert.True(result.Success);
        Assert.Contains("HelloFromEnv", result.Stdout);
    }

    [Fact]
    public async Task CommandExecution_WithTimeout_Completes()
    {
        // Act
        var result = await _sandbox.ExecuteCommandAsync(PlatformCommands.Echo("fast"), new CommandOptions
        {
            Timeout = TimeSpan.FromSeconds(5)
        });

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
    }

    [UnixOnlyFact]
    public async Task CommandExecution_CapturesStderr()
    {
        // Act (Unix-specific: >&2 redirection)
        var result = await _sandbox.ExecuteCommandAsync("echo error >&2");

        // Assert
        Assert.Contains("error", result.Stderr);
    }

    [Fact]
    public async Task FileExists_ReturnsFalseForNonExistent()
    {
        // Act
        var exists = await _sandbox.FileExistsAsync("nonexistent_file.txt");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public async Task GlobFiles_FindsMatchingPatterns()
    {
        // Arrange
        await _sandbox.WriteFileAsync("code1.cs", "// C# file 1");
        await _sandbox.WriteFileAsync("code2.cs", "// C# file 2");
        await _sandbox.WriteFileAsync("readme.md", "# Readme");
        await _sandbox.WriteFileAsync("config.json", "{}");

        // Act
        var csFiles = await _sandbox.GlobAsync("*.cs");
        var allFiles = await _sandbox.GlobAsync("*.*");

        // Assert
        Assert.Equal(2, csFiles.Count);
        Assert.All(csFiles, f => Assert.EndsWith(".cs", f));
        Assert.Equal(4, allFiles.Count);

        // Cleanup
        await _sandbox.DeleteFileAsync("code1.cs");
        await _sandbox.DeleteFileAsync("code2.cs");
        await _sandbox.DeleteFileAsync("readme.md");
        await _sandbox.DeleteFileAsync("config.json");
    }

    [Fact]
    public async Task MultipleFileOperations_InParallel()
    {
        // Arrange
        var fileCount = 10;
        var tasks = new List<Task>();

        // Act - Create files in parallel
        for (int i = 0; i < fileCount; i++)
        {
            var fileName = $"parallel_{i}.txt";
            var content = $"Content for file {i}";
            tasks.Add(_sandbox.WriteFileAsync(fileName, content));
        }

        await Task.WhenAll(tasks);

        // Assert - All files exist
        for (int i = 0; i < fileCount; i++)
        {
            var exists = await _sandbox.FileExistsAsync($"parallel_{i}.txt");
            Assert.True(exists, $"File parallel_{i}.txt should exist");
        }

        // Cleanup
        for (int i = 0; i < fileCount; i++)
        {
            await _sandbox.DeleteFileAsync($"parallel_{i}.txt");
        }
    }

    [Fact]
    public async Task LargeFile_ReadWrite()
    {
        // Arrange
        var fileName = "large_file.txt";
        var lineCount = 1000;
        var lines = Enumerable.Range(1, lineCount).Select(i => $"Line {i}: Some content here...").ToArray();
        var content = string.Join("\n", lines);

        // Act
        await _sandbox.WriteFileAsync(fileName, content);
        var readContent = await _sandbox.ReadFileAsync(fileName);

        // Assert
        Assert.Equal(content, readContent);
        Assert.Contains("Line 500", readContent);
        Assert.Contains("Line 1000", readContent);

        // Cleanup
        await _sandbox.DeleteFileAsync(fileName);
    }
}
