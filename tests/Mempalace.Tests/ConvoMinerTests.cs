namespace Mempalace.Tests;

public sealed class ConvoMinerTests
{
    // ── DetectFormat ──────────────────────────────────────────────────────────

    [Fact] public void DetectFormat_JsonlFile_ReturnsClaudeCodeJsonl() =>
        Assert.Equal(ConvoFormat.ClaudeCodeJsonl, ConvoMiner.DetectFormat("", "chat.jsonl"));

    [Fact] public void DetectFormat_CodexJsonl_ReturnsCodexJsonl() =>
        Assert.Equal(ConvoFormat.CodexJsonl,
            ConvoMiner.DetectFormat("{\"type\":\"session_meta\"}", "session.jsonl"));

    [Fact] public void DetectFormat_JsonArrayFile_ReturnsClaudeAiJson() =>
        Assert.Equal(ConvoFormat.ClaudeAiJson, ConvoMiner.DetectFormat("[{}]", "export.json"));

    [Fact] public void DetectFormat_SlackJsonArray_ReturnsSlackJson() =>
        Assert.Equal(ConvoFormat.SlackJson,
            ConvoMiner.DetectFormat("[{\"type\":\"message\",\"user\":\"U1\",\"text\":\"hi\"}]", "channel.json"));

    [Fact] public void DetectFormat_ChatGptJson_ReturnsChatGptJson() =>
        Assert.Equal(ConvoFormat.ChatGptJson, ConvoMiner.DetectFormat("{\"mapping\":{}}", "c.json"));

    [Fact] public void DetectFormat_PlainText_ReturnsPlainText() =>
        Assert.Equal(ConvoFormat.PlainText, ConvoMiner.DetectFormat("hello", "chat.txt"));

    // ── NormalizeToTranscript ─────────────────────────────────────────────────

    [Fact]
    public void Normalize_PlainText_ReturnsSameContent()
    {
        const string content = "> What is 2+2?\nFour.";
        var result = ConvoMiner.NormalizeToTranscript(content, ConvoFormat.PlainText);
        Assert.Equal(content, result);
    }

    [Fact]
    public void Normalize_JsonlFormat_ExtractsRolesAsQuoteMarkers()
    {
        var jsonl = """
            {"role":"user","content":"Hello"}
            {"role":"assistant","content":"Hi there"}
            """;
        var result = ConvoMiner.NormalizeToTranscript(jsonl, ConvoFormat.ClaudeCodeJsonl);
        Assert.Contains("> Hello", result);
        Assert.Contains("Hi there", result);
    }

    [Fact]
    public void Normalize_ClaudeAiJson_ExtractsTurns()
    {
        var json = """[{"chat_messages":[{"sender":"human","text":"Q"},{"sender":"assistant","text":"A"}]}]""";
        var result = ConvoMiner.NormalizeToTranscript(json, ConvoFormat.ClaudeAiJson);
        Assert.Contains("> Q", result);
        Assert.Contains("A", result);
    }

    [Fact]
    public void Normalize_MalformedJson_ReturnsFallback()
    {
        var result = ConvoMiner.NormalizeToTranscript("{bad json", ConvoFormat.ClaudeAiJson);
        Assert.Equal("{bad json", result);
    }

    [Fact]
    public void Normalize_CodexJsonl_ExtractsUserAndAgentMessages()
    {
        var jsonl = """
            {"type":"session_meta","session_id":"abc"}
            {"type":"event_msg","payload":{"type":"user_message","message":"Hello codex"}}
            {"type":"event_msg","payload":{"type":"agent_message","message":"Hi from agent"}}
            """;
        var result = ConvoMiner.NormalizeToTranscript(jsonl, ConvoFormat.CodexJsonl);
        Assert.Contains("> Hello codex", result);
        Assert.Contains("Hi from agent", result);
    }

    [Fact]
    public void Normalize_CodexJsonl_WithoutSessionMeta_ReturnsFallback()
    {
        // No session_meta line → not a valid Codex session, return as-is
        var jsonl = """
            {"type":"event_msg","payload":{"type":"user_message","message":"hello"}}
            {"type":"event_msg","payload":{"type":"agent_message","message":"world"}}
            """;
        var result = ConvoMiner.NormalizeToTranscript(jsonl, ConvoFormat.CodexJsonl);
        Assert.Equal(jsonl, result);
    }

    [Fact]
    public void Normalize_CodexJsonl_SkipsNonEventMsgEntries()
    {
        var jsonl = """
            {"type":"session_meta"}
            {"type":"tool_call","payload":{"type":"user_message","message":"ignored"}}
            {"type":"event_msg","payload":{"type":"user_message","message":"kept"}}
            {"type":"event_msg","payload":{"type":"agent_message","message":"reply"}}
            """;
        var result = ConvoMiner.NormalizeToTranscript(jsonl, ConvoFormat.CodexJsonl);
        Assert.DoesNotContain("ignored", result);
        Assert.Contains("> kept", result);
    }

    [Fact]
    public void Normalize_CodexJsonl_SkipsEmptyMessages()
    {
        var jsonl = """
            {"type":"session_meta"}
            {"type":"event_msg","payload":{"type":"user_message","message":"  "}}
            {"type":"event_msg","payload":{"type":"user_message","message":"real question"}}
            {"type":"event_msg","payload":{"type":"agent_message","message":"real answer"}}
            """;
        var result = ConvoMiner.NormalizeToTranscript(jsonl, ConvoFormat.CodexJsonl);
        var lines = result.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        // Only the non-empty user + agent lines should appear
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Normalize_SlackJson_ExtractsMessages()
    {
        var json = """
            [
              {"type":"message","user":"U1","text":"Hello team"},
              {"type":"message","user":"U2","text":"Hey there"}
            ]
            """;
        var result = ConvoMiner.NormalizeToTranscript(json, ConvoFormat.SlackJson);
        Assert.Contains("Hello team", result);
        Assert.Contains("Hey there", result);
    }

    [Fact]
    public void Normalize_SlackJson_FirstUserGetsUserRole()
    {
        var json = """[{"type":"message","user":"U1","text":"first message"}]""";
        var result = ConvoMiner.NormalizeToTranscript(json, ConvoFormat.SlackJson);
        Assert.Contains("> first message", result);
    }

    [Fact]
    public void Normalize_SlackJson_AlternatesRolesForDifferentUsers()
    {
        var json = """
            [
              {"type":"message","user":"U1","text":"from user one"},
              {"type":"message","user":"U2","text":"from user two"}
            ]
            """;
        var result = ConvoMiner.NormalizeToTranscript(json, ConvoFormat.SlackJson);
        // U1 → user (prefixed with >), U2 → assistant (no prefix)
        Assert.Contains("> from user one", result);
        Assert.Contains("from user two", result);
        Assert.DoesNotContain("> from user two", result);
    }

    [Fact]
    public void Normalize_SlackJson_SkipsNonMessageTypes()
    {
        var json = """
            [
              {"type":"channel_join","user":"U1","text":"joined"},
              {"type":"message","user":"U1","text":"real message"}
            ]
            """;
        var result = ConvoMiner.NormalizeToTranscript(json, ConvoFormat.SlackJson);
        Assert.DoesNotContain("joined", result);
        Assert.Contains("real message", result);
    }

    [Fact]
    public void Normalize_SlackJson_MalformedJson_ReturnsFallback()
    {
        var result = ConvoMiner.NormalizeToTranscript("not json at all", ConvoFormat.SlackJson);
        Assert.Equal("not json at all", result);
    }

    // ── ChunkExchanges ────────────────────────────────────────────────────────

    [Fact]
    public void ChunkExchanges_WithQuoteMarkers_ChunksByExchange()
    {
        var content = string.Join("\n",
            "> First question",
            "First answer here with some length to pass min size threshold",
            "",
            "> Second question",
            "Second answer here with some length to pass min size threshold");

        var chunks = ConvoMiner.ChunkExchanges(content);
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void ChunkExchanges_NoQuoteMarkers_FallsBackToParagraphs()
    {
        var content = string.Join("\n\n",
            Enumerable.Range(0, 5).Select(i => $"Paragraph {i}: " + new string('x', 40)));
        var chunks = ConvoMiner.ChunkExchanges(content);
        Assert.True(chunks.Count >= 2);
    }

    [Fact]
    public void ChunkExchanges_IndexesAreSequential()
    {
        var content = string.Join("\n",
            "> Q1", "A1 " + new string('a', 40),
            "> Q2", "A2 " + new string('b', 40),
            "> Q3", "A3 " + new string('c', 40));
        var chunks = ConvoMiner.ChunkExchanges(content);
        for (int i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkIndex);
    }

    // ── DetectConvoRoom ───────────────────────────────────────────────────────

    [Fact]
    public void DetectConvoRoom_TechnicalContent_ReturnsTechnical()
    {
        var result = ConvoMiner.DetectConvoRoom("We need to fix this bug in the Python code and deploy the api");
        Assert.Equal("technical", result);
    }

    [Fact]
    public void DetectConvoRoom_PlanningContent_ReturnsPlanning()
    {
        var result = ConvoMiner.DetectConvoRoom("What is the roadmap for the next sprint? Milestone backlog spec");
        Assert.Equal("planning", result);
    }

    [Fact]
    public void DetectConvoRoom_NoKeywords_ReturnsGeneral()
    {
        var result = ConvoMiner.DetectConvoRoom("sunshine rainbow kittens fluffy clouds");
        Assert.Equal("general", result);
    }

    // ── TopicKeywords completeness ────────────────────────────────────────────

    [Fact]
    public void TopicKeywords_ContainsFiveRooms() =>
        Assert.Equal(5, ConvoMiner.TopicKeywords.Count);

    [Fact]
    public void TopicKeywords_TechnicalContainsCode() =>
        Assert.Contains("code", ConvoMiner.TopicKeywords["technical"]);
}
