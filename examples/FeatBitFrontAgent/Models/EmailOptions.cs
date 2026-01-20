using Microsoft.Extensions.Configuration;

namespace Kode.Agent.WebApiAssistant.Models;

/// <summary>
/// Email server configuration
/// </summary>
public class EmailServerOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Email options from appsettings.json
/// </summary>
public class EmailOptions
{
    /// <summary>
    /// Whether email functionality is enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// IMAP configuration for receiving emails
    /// </summary>
    public EmailServerOptions Imap { get; set; } = new();

    /// <summary>
    /// SMTP configuration for sending emails
    /// </summary>
    public EmailServerOptions Smtp { get; set; } = new();

    /// <summary>
    /// Default email address for sending
    /// </summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>
    /// Default sender name
    /// </summary>
    public string FromName { get; set; } = "AI Assistant";

    public static EmailOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Email");
        var options = new EmailOptions
        {
            Enabled = section.GetValue<bool>("Enabled")
        };

        // IMAP configuration
        var imapSection = section.GetSection("Imap");
        var imapPort = imapSection.GetValue<int?>("Port");
        var imapUseSsl = imapSection.GetValue<bool?>("UseSsl");
        options.Imap = new EmailServerOptions
        {
            Host = imapSection["Host"] ?? Environment.GetEnvironmentVariable("IMAP_HOST") ?? "",
            Port = imapPort ?? 993,
            UseSsl = imapUseSsl ?? true,
            Username = imapSection["Username"] ?? Environment.GetEnvironmentVariable("IMAP_USERNAME") ?? "",
            Password = imapSection["Password"] ?? Environment.GetEnvironmentVariable("IMAP_PASSWORD") ?? ""
        };

        // SMTP configuration
        var smtpSection = section.GetSection("Smtp");
        var smtpPort = smtpSection.GetValue<int?>("Port");
        var smtpUseSsl = smtpSection.GetValue<bool?>("UseSsl");
        options.Smtp = new EmailServerOptions
        {
            Host = smtpSection["Host"] ?? Environment.GetEnvironmentVariable("SMTP_HOST") ?? "",
            Port = smtpPort ?? 587,
            UseSsl = smtpUseSsl ?? true,
            Username = smtpSection["Username"] ?? Environment.GetEnvironmentVariable("SMTP_USERNAME") ?? "",
            Password = smtpSection["Password"] ?? Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? ""
        };

        options.FromAddress = section["FromAddress"] ?? Environment.GetEnvironmentVariable("EMAIL_FROM") ?? "";
        options.FromName = section["FromName"] ?? "AI Assistant";

        return options;
    }

    public bool IsConfigured()
    {
        return Enabled &&
               !string.IsNullOrEmpty(Imap.Host) &&
               !string.IsNullOrEmpty(Imap.Username) &&
               !string.IsNullOrEmpty(Imap.Password);
    }
}
