using MailBroadcast.Worker.Models;

// File: EmailTemplate.cs — performs literal substitutions for supported campaign message placeholders.
namespace MailBroadcast.Worker.Services;

/// <summary>Renders supported recipient fields into configurable email templates.</summary>
public static class EmailTemplate
{
    /// <summary>Replaces {Name} and {CampaignId} placeholders, HTML-encoding values for HTML output.</summary>
    /// <param name="template">The subject or body template string.</param>
    /// <param name="recipient">The recipient whose campaign details are inserted.</param>
    /// <param name="isHtml">When true, encodes replacement values for safe insertion into HTML text content.</param>
    /// <returns>The rendered template.</returns>
    public static string Render(string template, CampaignRecipient recipient, bool isHtml = false)
    {
        var name = isHtml ? System.Net.WebUtility.HtmlEncode(recipient.Name) : recipient.Name;
        var campaignId = isHtml ? System.Net.WebUtility.HtmlEncode(recipient.CampaignId) : recipient.CampaignId;
        return template.Replace("{Name}", name, StringComparison.Ordinal)
            .Replace("{CampaignId}", campaignId, StringComparison.Ordinal);
    }
}