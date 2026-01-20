using System.Net;

namespace Kode.Agent.WebApiAssistant.Middleware;

/// <summary>
/// 文件上传中间件
/// </summary>
public class FileUploadMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FileUploadMiddleware> _logger;
    private readonly long _maxFileSize;
    private readonly string[] _allowedExtensions;
    private readonly string _uploadDirectory;

    public FileUploadMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<FileUploadMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        _maxFileSize = configuration.GetValue<long>("FileUpload:MaxFileSize", 100 * 1024 * 1024); // 默认 100MB
        _allowedExtensions = configuration.GetSection("FileUpload:AllowedExtensions")
            .Get<string[]>() ?? new[] { ".txt", ".md", ".pdf", ".png", ".jpg", ".jpeg" };
        _uploadDirectory = configuration["FileUpload:Directory"] ?? "uploads";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 只处理文件上传请求
        if (!IsMultipartContentType(context.Request.ContentType))
        {
            await _next(context);
            return;
        }

        try
        {
            var form = await context.Request.ReadFormAsync();
            var files = form.Files;

            if (files.Count == 0)
            {
                await _next(context);
                return;
            }

            var uploadedFiles = new List<string>();

            foreach (var file in files)
            {
                // 验证文件
                var validationError = ValidateFile(file);
                if (!string.IsNullOrEmpty(validationError))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = new
                        {
                            message = validationError,
                            type = "validation_error"
                        }
                    });
                    return;
                }

                // 保存文件
                var filePath = await SaveFileAsync(file);
                uploadedFiles.Add(filePath);
            }

            // 将上传的文件路径添加到请求特性中
            context.Items["UploadedFiles"] = uploadedFiles;

            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File upload failed");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "File upload failed",
                    type = "server_error"
                }
            });
        }
    }

    /// <summary>
    /// 验证文件
    /// </summary>
    private string? ValidateFile(IFormFile file)
    {
        // 检查文件大小
        if (file.Length > _maxFileSize)
        {
            return $"File size exceeds maximum allowed size of {_maxFileSize / (1024 * 1024)}MB";
        }

        // 检查文件扩展名
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedExtensions.Contains(extension))
        {
            return $"File extension '{extension}' is not allowed";
        }

        // 检查 MIME 类型
        var allowedMimeTypes = new[]
        {
            "text/plain",
            "text/markdown",
            "application/pdf",
            "image/png",
            "image/jpeg"
        };

        if (!allowedMimeTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return $"MIME type '{file.ContentType}' is not allowed";
        }

        return null;
    }

    /// <summary>
    /// 保存文件
    /// </summary>
    private async Task<string> SaveFileAsync(IFormFile file)
    {
        // 创建上传目录
        Directory.CreateDirectory(_uploadDirectory);

        // 生成唯一文件名
        var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
        var filePath = Path.Combine(_uploadDirectory, fileName);

        // 保存文件
        using var stream = File.Create(filePath);
        await file.CopyToAsync(stream);

        _logger.LogInformation("File uploaded: {FilePath} ({Size} bytes)", filePath, file.Length);

        return filePath;
    }

    /// <summary>
    /// 检查是否为 multipart 内容类型
    /// </summary>
    private static bool IsMultipartContentType(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
            && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 扩展方法
/// </summary>
public static class FileUploadMiddlewareExtensions
{
    public static IApplicationBuilder UseFileUpload(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<FileUploadMiddleware>();
    }
}
