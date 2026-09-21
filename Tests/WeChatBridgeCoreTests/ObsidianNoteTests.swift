import Foundation
import XCTest
@testable import WeChatBridgeCore

final class ObsidianNoteTests: XCTestCase {
    func testRendersFrontmatterAndTranscriptLinks() throws {
        let body = """
        ·甲
        2026年9月20日 09:10
        第一条

        ·乙
        2026年9月20日 09:11
        第二条
        """
        let transcript = WeChatNativeArchive.Transcript(
            path: "聊天记录.txt",
            body: body,
            records: try WeChatTranscriptRecord.parse(body, timeZone: TimeZone(secondsFromGMT: 0)!)
        )
        XCTAssertEqual(
            ObsidianNote.title(
                chatName: "项目群 (8)",
                transcript: transcript,
                archiveName: "Zip归档.zip"
            ),
            "项目群 (8)的聊天"
        )
        let note = ObsidianNote.render(
            title: "项目群 (8)的聊天",
            chatName: "项目群 (8)",
            sceneName: "项目周会",
            createdAt: Date(timeIntervalSince1970: 0),
            transcript: transcript,
            archiveName: "项目群.zip",
            timeZone: TimeZone(secondsFromGMT: 0)!
        )

        XCTAssertTrue(note.contains("title: \"项目群 (8)的聊天\""))
        XCTAssertTrue(note.contains("chat: \"项目群 (8)\""))
        XCTAssertTrue(note.contains("scene: \"项目周会\""))
        XCTAssertTrue(note.contains("archive: \"附件/项目群.zip\""))
        XCTAssertTrue(note.contains("[[附件/项目群.zip]]"))
        XCTAssertTrue(note.contains("**甲**"))
        XCTAssertTrue(note.contains("第一条"))
    }

    func testEmbedsAttachmentAtItsMessage() throws {
        let body = """
        ·甲
        2026年9月20日 09:10
        [图片] 微信图片_202609202355_1.jpg
        """
        let transcript = WeChatNativeArchive.Transcript(
            path: "聊天记录.txt",
            body: body,
            records: try WeChatTranscriptRecord.parse(body, timeZone: TimeZone(secondsFromGMT: 0)!)
        )
        let note = ObsidianNote.render(
            title: "项目群的聊天",
            chatName: "项目群",
            sceneName: nil,
            createdAt: Date(timeIntervalSince1970: 0),
            transcript: transcript,
            archiveName: "项目群.zip",
            attachments: ["微信图片_202609202355_1.jpg": "微信图片_202609202355_1.jpg"],
            timeZone: TimeZone(secondsFromGMT: 0)!
        )

        XCTAssertTrue(note.contains("""
        [图片] 微信图片_202609202355_1.jpg

        ![[附件/微信图片_202609202355_1.jpg]]
        """))
    }

    func testLongerAttachmentNameWinsWithoutAlsoMatchingASubstring() throws {
        let body = """
        ·甲
        2026年9月20日 09:10
        [图片] 微信图片_202609202355_11.jpg
        """
        let transcript = WeChatNativeArchive.Transcript(
            path: "聊天记录.txt",
            body: body,
            records: try WeChatTranscriptRecord.parse(body, timeZone: TimeZone(secondsFromGMT: 0)!)
        )
        let note = ObsidianNote.render(
            title: "项目群的聊天",
            chatName: "项目群",
            sceneName: nil,
            createdAt: Date(timeIntervalSince1970: 0),
            transcript: transcript,
            archiveName: "项目群.zip",
            attachments: [
                "微信图片_202609202355_1.jpg": "微信图片_202609202355_1.jpg",
                "微信图片_202609202355_11.jpg": "微信图片_202609202355_11.jpg",
            ],
            timeZone: TimeZone(secondsFromGMT: 0)!
        )

        XCTAssertTrue(note.contains("![[附件/微信图片_202609202355_11.jpg]]"))
        XCTAssertFalse(note.contains("![[附件/微信图片_202609202355_1.jpg]]"))
    }

    func testOCRTitleWinsOverTranscriptParticipants() {
        let body = """
        ·甲
        2026年9月20日 09:10
        第一条
        """
        let transcript = WeChatNativeArchive.Transcript(
            path: "聊天记录.txt",
            body: body,
            records: try! WeChatTranscriptRecord.parse(body, timeZone: TimeZone(secondsFromGMT: 0)!)
        )
        XCTAssertEqual(
            ObsidianNote.title(
                chatName: "AI先行者联盟",
                transcript: transcript,
                archiveName: "微信聊天记录.zip"
            ),
            "AI先行者联盟的聊天"
        )
    }

    func testQuotesYamlSpecialCharacters() {
        XCTAssertEqual(
            ObsidianNote.title(chatName: nil, transcript: nil, archiveName: "Zip归档.zip"),
            "Zip归档"
        )
        let note = ObsidianNote.render(
            title: "a \"quoted\" title",
            chatName: nil,
            sceneName: nil,
            createdAt: Date(timeIntervalSince1970: 0),
            transcript: nil,
            archiveName: "chat.zip"
        )
        XCTAssertTrue(note.contains("title: \"a \\\"quoted\\\" title\""))
        XCTAssertTrue(note.contains("未能从原始归档中解析聊天文本"))
    }
}
