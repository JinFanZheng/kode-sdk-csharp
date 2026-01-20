using Kode.Agent.WebApiAssistant.Models.Requests;
using Kode.Agent.WebApiAssistant.Models.Responses;
using Kode.Agent.WebApiAssistant.Services;
using Microsoft.AspNetCore.Mvc;

namespace Kode.Agent.WebApiAssistant.Controllers;

/// <summary>
/// 会话管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly IUserService _userService;
    private readonly ILogger<SessionsController> _logger;

    public SessionsController(
        ISessionService sessionService,
        IUserService userService,
        ILogger<SessionsController> logger)
    {
        _sessionService = sessionService;
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// 获取用户的所有会话
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <returns>会话列表</returns>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> ListSessions(
        [FromQuery] string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return BadRequest(new { error = "userId is required" });
        }

        var sessions = await _sessionService.ListSessionsAsync(userId);
        var response = sessions.Select(SessionResponse.FromEntity).ToList();

        return Ok(response);
    }

    /// <summary>
    /// 获取指定会话详情
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>会话详情</returns>
    [HttpGet("{sessionId}")]
    public async Task<ActionResult<SessionResponse>> GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { error = "sessionId is required" });
        }

        var session = await _sessionService.GetSessionAsync(sessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found" });
        }

        return Ok(SessionResponse.FromEntity(session));
    }

    /// <summary>
    /// 创建新会话
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="request">创建请求</param>
    /// <returns>新创建的会话</returns>
    [HttpPost]
    public async Task<ActionResult<SessionResponse>> CreateSession(
        [FromQuery] string userId,
        [FromBody] SessionCreateRequest? request)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return BadRequest(new { error = "userId is required" });
        }

        var session = await _sessionService.CreateSessionAsync(userId, request?.Title);
        return CreatedAtAction(
            nameof(GetSession),
            new { sessionId = session.SessionId },
            SessionResponse.FromEntity(session));
    }

    /// <summary>
    /// 更新会话
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="request">更新请求</param>
    /// <returns>更新后的会话</returns>
    [HttpPatch("{sessionId}")]
    public async Task<ActionResult<SessionResponse>> UpdateSession(
        string sessionId,
        [FromBody] SessionUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { error = "sessionId is required" });
        }

        var session = await _sessionService.GetSessionAsync(sessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found" });
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            await _sessionService.UpdateSessionTitleAsync(sessionId, request.Title);
        }

        session = await _sessionService.GetSessionAsync(sessionId);
        return Ok(SessionResponse.FromEntity(session!));
    }

    /// <summary>
    /// 删除会话
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <returns>No Content</returns>
    [HttpDelete("{sessionId}")]
    public async Task<ActionResult> DeleteSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return BadRequest(new { error = "sessionId is required" });
        }

        var session = await _sessionService.GetSessionAsync(sessionId);
        if (session == null)
        {
            return NotFound(new { error = "Session not found" });
        }

        await _sessionService.DeleteSessionAsync(sessionId);
        return NoContent();
    }
}
