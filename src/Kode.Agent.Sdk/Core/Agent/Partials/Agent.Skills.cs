using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Templates;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Agent;

// Skill discovery / activation surface. Covers:
//   - public ActivateSkillAsync (host- or tool-driven activation)
//   - private InitializeSkillsAsync (one-shot bootstrap during CreateAsync)
//   - private RemindAsync + WrapReminder (the <system-reminder> injection path
//     shared with ActivateSkill and the auto-activate paths)
public sealed partial class Agent
{
    public async Task<Skill> ActivateSkillAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_skillsManager == null)
        {
            throw new InvalidOperationException("Skills not configured for this agent");
        }

        var skill = await _skillsManager.ActivateAsync(name, SkillActivationSource.Agent, cancellationToken);

        var instructionsXml = SkillsInjector.ToActivatedXml(skill);
        await RemindAsync(
            instructionsXml,
            category: "general",
            skipStandardEnding: true,
            cancellationToken: cancellationToken);

        if (skill.AllowedTools is { Count: > 0 })
        {
            _permissionManager.GrantTools(skill.AllowedTools);
        }

        _eventBus.EmitMonitor(new SkillActivatedEvent
        {
            Type = "skill_activated",
            Skill = skill.Name,
            ActivatedBy = "agent",
            Timestamp = NowMs()
        });

        return skill;
    }

    private async Task InitializeSkillsAsync(CancellationToken cancellationToken)
    {
        if (_skillsManager != null) return;
        if (_config.Skills == null) return;
        if (_sandbox == null) return;

        _skillsManager = new SkillsManager(
            _config.Skills,
            _sandbox,
            _dependencies.Store,
            AgentId,
            _dependencies.LoggerFactory?.CreateLogger<SkillsManager>());

        try
        {
            await _skillsManager.RestoreStateAsync(cancellationToken);
        }
        catch
        {
            // best-effort: skills state is optional
        }

        var skills = await _skillsManager.DiscoverAsync(cancellationToken);
        if (skills.Count == 0) return;

        var injectionMode = _config.Skills?.InjectionMode ?? SkillsInjectionMode.Full;
        var skillsXml = SkillsInjector.ToPromptXml(skills, injectionMode);
        if (!string.IsNullOrWhiteSpace(skillsXml))
        {
            _systemPrompt = (_systemPrompt ?? string.Empty) + skillsXml;
        }

        _eventBus.EmitMonitor(new SkillDiscoveredEvent
        {
            Type = "skill_discovered",
            Skills = skills.Select(s => s.Name).ToList(),
            Timestamp = NowMs()
        });

        // 1. SkillsConfig.AutoActivate (config-driven path, does not require Template system)
        if (_config.Skills?.AutoActivate is { Count: > 0 })
        {
            var autoActivated = await _skillsManager.AutoActivateAsync(_config.Skills.AutoActivate, cancellationToken);
            if (autoActivated.Count > 0)
            {
                foreach (var skill in autoActivated)
                {
                    var xml = SkillsInjector.ToActivatedXml(skill);
                    await RemindAsync(xml, category: "general", skipStandardEnding: false, cancellationToken: cancellationToken);
                }

                var granted1 = autoActivated
                    .SelectMany(s => s.AllowedTools ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                _permissionManager.GrantTools(granted1);

                _eventBus.EmitMonitor(new SkillActivatedEvent
                {
                    Type = "skill_activated",
                    Skill = string.Join(", ", autoActivated.Select(s => s.Name)),
                    ActivatedBy = "auto",
                    Timestamp = NowMs()
                });
            }
        }

        // 2. Template runtime skills behavior (autoActivate / recommend).
        TemplateSkillsConfig? templateSkills = null;
        if (_dependencies.TemplateRegistry != null &&
            !string.IsNullOrWhiteSpace(_config.TemplateId) &&
            _dependencies.TemplateRegistry.TryGet(_config.TemplateId!, out var template) &&
            template?.Runtime?.Skills != null)
        {
            templateSkills = template.Runtime.Skills;
        }

        if (templateSkills?.AutoActivate is { Count: > 0 })
        {
            var autoActivated = await _skillsManager.AutoActivateAsync(templateSkills.AutoActivate, cancellationToken);
            if (autoActivated.Count > 0)
            {
                foreach (var skill in autoActivated)
                {
                    var xml = SkillsInjector.ToActivatedXml(skill);
                    await RemindAsync(xml, category: "general", skipStandardEnding: false, cancellationToken: cancellationToken);
                }

                var granted2 = autoActivated
                    .SelectMany(s => s.AllowedTools ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                _permissionManager.GrantTools(granted2);

                _eventBus.EmitMonitor(new SkillActivatedEvent
                {
                    Type = "skill_activated",
                    Skill = string.Join(", ", autoActivated.Select(s => s.Name)),
                    ActivatedBy = "auto",
                    Timestamp = NowMs()
                });
            }
        }

        if (templateSkills?.Recommend is { Count: > 0 })
        {
            var recommended = templateSkills.Recommend
                .Where(name => _skillsManager.Get(name) != null && !_skillsManager.IsActivated(name))
                .ToList();

            if (recommended.Count > 0)
            {
                var recommendXml =
                    $"\n<recommended_skills>\nConsider activating these skills if relevant to your task: {string.Join(", ", recommended)}\n</recommended_skills>\n";
                _systemPrompt = (_systemPrompt ?? string.Empty) + recommendXml;
            }
        }
    }

    private static string WrapReminder(string content, bool skipStandardEnding)
    {
        if (skipStandardEnding) return content;
        return string.Join("\n", new[]
        {
            "<system-reminder>",
            content,
            "",
            "This is a system reminder. DO NOT respond to this message directly.",
            "DO NOT mention this reminder to the user.",
            "Continue with your current task.",
            "</system-reminder>"
        });
    }

    private async Task RemindAsync(
        string content,
        string category,
        bool skipStandardEnding,
        CancellationToken cancellationToken)
    {
        var payload = WrapReminder(content, skipStandardEnding);

        _messageQueue.Send(content, new SendOptions
        {
            Kind = PendingKind.Reminder,
            Reminder = new ReminderOptions
            {
                SkipStandardEnding = skipStandardEnding,
                Category = category
            }
        });
        await _messageQueue.FlushAsync(cancellationToken);

        _eventBus.EmitMonitor(new ReminderSentEvent
        {
            Type = "reminder_sent",
            Category = category,
            Content = payload
        });
    }
}
