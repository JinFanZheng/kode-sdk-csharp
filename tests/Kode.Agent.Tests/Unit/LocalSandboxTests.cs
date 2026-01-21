using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Tests.Helpers;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public class LocalSandboxTests
{
    [Fact]
    public async Task ExecuteCommandAsync_ReturnsOutput()
    {
        // Arrange
        await using var sandbox = new LocalSandbox();
        
        // Act
        var result = await sandbox.ExecuteCommandAsync(PlatformCommands.Echo("Hello"));
        
        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hello", result.Stdout);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task WriteFileAsync_ThenReadFileAsync_ReturnsContent()
    {
        // Arrange
        await using var sandbox = new LocalSandbox(new SandboxOptions
        {
            WorkingDirectory = Path.GetTempPath()
        });
        
        var testFile = $"test_{Guid.NewGuid():N}.txt";
        var content = "Test content";
        
        try
        {
            // Act
            await sandbox.WriteFileAsync(testFile, content);
            var readContent = await sandbox.ReadFileAsync(testFile);
            
            // Assert
            Assert.Equal(content, readContent);
        }
        finally
        {
            // Cleanup
            await sandbox.DeleteFileAsync(testFile);
        }
    }

    [Fact]
    public async Task FileExistsAsync_ReturnsTrueForExistingFile()
    {
        // Arrange
        await using var sandbox = new LocalSandbox(new SandboxOptions
        {
            WorkingDirectory = Path.GetTempPath()
        });
        
        var testFile = $"test_{Guid.NewGuid():N}.txt";
        await sandbox.WriteFileAsync(testFile, "content");
        
        try
        {
            // Act
            var exists = await sandbox.FileExistsAsync(testFile);
            
            // Assert
            Assert.True(exists);
        }
        finally
        {
            await sandbox.DeleteFileAsync(testFile);
        }
    }

    [Fact]
    public async Task ListDirectoryAsync_ReturnsEntries()
    {
        // Arrange
        await using var sandbox = new LocalSandbox(new SandboxOptions
        {
            WorkingDirectory = Path.GetTempPath()
        });
        
        var testDir = $"testdir_{Guid.NewGuid():N}";
        var testFile = Path.Combine(testDir, "file.txt");
        
        try
        {
            await sandbox.CreateDirectoryAsync(testDir);
            await sandbox.WriteFileAsync(testFile, "content");
            
            // Act
            var entries = await sandbox.ListDirectoryAsync(testDir);
            
            // Assert
            Assert.Single(entries);
            Assert.Equal("file.txt", entries[0].Name);
            Assert.False(entries[0].IsDirectory);
        }
        finally
        {
            await sandbox.DeleteDirectoryAsync(testDir, recursive: true);
        }
    }
}
