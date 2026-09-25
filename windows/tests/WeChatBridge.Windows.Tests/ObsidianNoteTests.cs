using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>Ported from ObsidianNoteTests.swift.</summary>
public sealed class ObsidianNoteTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void RendersFrontmatterAndTranscriptLinks()
    {
        var body = "·甲\n2026年9月20日 09:10\n第一条\n\n·乙\n2026年9月20日 09:11\n第二条\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        Assert.Equal(
            "项目群 (8)的聊天",
            ObsidianNote.Title("项目群 (8)", transcript, "Zip归档.zip"));

        var note = ObsidianNote.Render(
            "项目群 (8)的聊天",
            "项目群 (8)",
            "项目周会",
            DateTimeOffset.UnixEpoch,
            transcript,
            "项目群.zip",
            timeZone: Utc);

        Assert.Contains("title: \"项目群 (8)的聊天\"", note);
        Assert.Contains("chat: \"项目群 (8)\"", note);
        Assert.Contains("scene: \"项目周会\"", note);
        Assert.Contains("archive: \"附件/项目群.zip\"", note);
        Assert.Contains("[[附件/项目群.zip]]", note);
        Assert.Contains("**甲**", note);
        Assert.Contains("第一条", note);
    }

    [Fact]
    public void EmbedsAttachmentAtItsMessage()
    {
        var body = "·甲\n2026年9月20日 09:10\n[图片] 微信图片_202609202355_1.jpg\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        var note = ObsidianNote.Render(
            "项目群的聊天",
            "项目群",
            null,
            DateTimeOffset.UnixEpoch,
            transcript,
            "项目群.zip",
            attachments: new Dictionary<string, string>
            {
                ["微信图片_202609202355_1.jpg"] = "微信图片_202609202355_1.jpg",
            },
            timeZone: Utc);

        Assert.Contains("[图片] 微信图片_202609202355_1.jpg\n\n![[附件/微信图片_202609202355_1.jpg]]", note);
    }

    [Fact]
    public void LongerAttachmentNameWinsWithoutAlsoMatchingASubstring()
    {
        var body = "·甲\n2026年9月20日 09:10\n[图片] 微信图片_202609202355_11.jpg\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        var note = ObsidianNote.Render(
            "项目群的聊天",
            "项目群",
            null,
            DateTimeOffset.UnixEpoch,
            transcript,
            "项目群.zip",
            attachments: new Dictionary<string, string>
            {
                ["微信图片_202609202355_1.jpg"] = "微信图片_202609202355_1.jpg",
                ["微信图片_202609202355_11.jpg"] = "微信图片_202609202355_11.jpg",
            },
            timeZone: Utc);

        Assert.Contains("![[附件/微信图片_202609202355_11.jpg]]", note);
        Assert.DoesNotContain("![[附件/微信图片_202609202355_1.jpg]]", note);
    }

    [Fact]
    public void OcrTitleWinsOverTranscriptParticipants()
    {
        var body = "·甲\n2026年9月20日 09:10\n第一条\n";
        var transcript = new WeChatNativeArchive.Transcript(
            "聊天记录.txt",
            body,
            WeChatTranscriptRecord.Parse(body, Utc));

        Assert.Equal(
            "AI先行者联盟的聊天",
            ObsidianNote.Title("AI先行者联盟", transcript, "微信聊天记录.zip"));
    }

    [Fact]
    public void QuotesYamlSpecialCharacters()
    {
        Assert.Equal("Zip归档", ObsidianNote.Title(null, null, "Zip归档.zip"));

        var note = ObsidianNote.Render(
            "a \"quoted\" title",
            null,
            null,
            DateTimeOffset.UnixEpoch,
            null,
            "chat.zip",
            timeZone: Utc);

        Assert.Contains("title: \"a \\\"quoted\\\" title\"", note);
        Assert.Contains("未能从原始归档中解析聊天文本", note);
    }
}
