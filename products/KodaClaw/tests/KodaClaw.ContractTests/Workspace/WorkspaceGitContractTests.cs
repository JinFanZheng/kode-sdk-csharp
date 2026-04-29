using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

/// <summary>
/// L3 契约测试 — WorkspaceGit* DTO JSON 序列化格式。
/// </summary>
public sealed class WorkspaceGitContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void WorkspaceGitCommit_round_trips_correctly()
    {
        const string json = """
            {
              "hash":        "abc1234567890abcdef1234567890abcdef123456",
              "shortHash":   "abc1234",
              "message":     "workspace(identity)[agent]: update persona",
              "author":      "KodaClaw",
              "committedAt": "2026-03-24T12:00:00+00:00",
              "changedFiles": ["workspace/IDENTITY.md", "workspace/SOUL.md"]
            }
            """;

        var commit = JsonSerializer.Deserialize<WorkspaceGitCommit>(json, JsonOptions)!;

        commit.Hash.Should().Be("abc1234567890abcdef1234567890abcdef123456");
        commit.ShortHash.Should().Be("abc1234");
        commit.Message.Should().Be("workspace(identity)[agent]: update persona");
        commit.Author.Should().Be("KodaClaw");
        commit.CommittedAt.Should().Be(new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero));
        commit.ChangedFiles.Should().Equal("workspace/IDENTITY.md", "workspace/SOUL.md");
    }

    [Fact]
    public void WorkspaceGitLogResponse_round_trips_correctly()
    {
        const string json = """
            {
              "commits": [
                {
                  "hash":        "aaa0000000000000000000000000000000000001",
                  "shortHash":   "aaa0000",
                  "message":     "workspace(memory)[agent]: append daily 2026-03-24",
                  "author":      "KodaClaw",
                  "committedAt": "2026-03-24T08:00:00+00:00",
                  "changedFiles": ["workspace/MEMORY.md"]
                }
              ]
            }
            """;

        var response = JsonSerializer.Deserialize<WorkspaceGitLogResponse>(json, JsonOptions)!;

        response.Commits.Should().HaveCount(1);
        response.Commits[0].ShortHash.Should().Be("aaa0000");
        response.Commits[0].ChangedFiles.Should().ContainSingle("workspace/MEMORY.md");
    }

    [Fact]
    public void WorkspaceGitLogResponse_with_empty_commits_round_trips()
    {
        const string json = """{ "commits": [] }""";

        var response = JsonSerializer.Deserialize<WorkspaceGitLogResponse>(json, JsonOptions)!;

        response.Commits.Should().BeEmpty();
    }

    [Fact]
    public void WorkspaceGitRevertFileRequest_round_trips_correctly()
    {
        const string json = """
            {
              "hash":     "abc1234567890abcdef1234567890abcdef123456",
              "filePath": "workspace/IDENTITY.md"
            }
            """;

        var request = JsonSerializer.Deserialize<WorkspaceGitRevertFileRequest>(json, JsonOptions)!;

        request.Hash.Should().Be("abc1234567890abcdef1234567890abcdef123456");
        request.FilePath.Should().Be("workspace/IDENTITY.md");
    }

    [Fact]
    public void WorkspaceGitRevertFileResponse_round_trips_correctly()
    {
        const string json = """{ "newHash": "def9876543210fedcba9876543210fedcba98765" }""";

        var response = JsonSerializer.Deserialize<WorkspaceGitRevertFileResponse>(json, JsonOptions)!;

        response.NewHash.Should().Be("def9876543210fedcba9876543210fedcba98765");
    }

    [Fact]
    public void WorkspaceGitCommit_serialises_to_camelCase()
    {
        var commit = new WorkspaceGitCommit(
            Hash: "abc1234567890abcdef1234567890abcdef123456",
            ShortHash: "abc1234",
            Message: "test msg",
            Author: "KodaClaw",
            CommittedAt: new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero),
            ChangedFiles: ["workspace/SOUL.md"]);

        var json = JsonSerializer.Serialize(commit, JsonOptions);

        json.Should().Contain("\"hash\"");
        json.Should().Contain("\"shortHash\"");
        json.Should().Contain("\"committedAt\"");
        json.Should().Contain("\"changedFiles\"");
    }
}
