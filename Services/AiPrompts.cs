using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ScriptAppDashboard.Services;

/// <summary>The prompts behind each AI feature, in one place so they are easy to tune.</summary>
public static class AiPrompts
{
    private const string MarkdownOnly = "Output only the result as Markdown — no preamble, no closing remarks, no ``` fence around it.";

    // ---- work log --------------------------------------------------------------------

    public static string WorkReport(string context) => $"""
        Below is an automatic log of one person's computer activity: time per app, the windows and browser tabs they had
        in front, and a timeline of window sessions. Window titles usually name the file, project, ticket, page or meeting.

        Write a short standup-style report of this period in Markdown with these sections:
        ## Worked on — bullets grouped by project or task (infer them from the window titles), each with the approximate time
        ## Focus — when the deep-work stretches were, and where time went to switching, email, chat or browsing
        ## Next — 1–3 likely follow-ups, only if the log clearly suggests them
        Stay under 250 words. Use only what the log shows; don't invent work. Mention nothing about the log format itself.
        {AiService.LanguageRule} {MarkdownOnly}

        {context}
        """;

    public static string Timesheet(string context) => $"""
        Below is an automatic log of one person's computer activity: time per app, the windows and browser tabs they had
        in front, and a timeline of window sessions.

        Draft a timesheet from it. Group the activity into projects / tasks (infer them from file names, repositories,
        tickets, documents and site names in the window titles). Output a Markdown table with the columns
        Date | Project / task | Hours | What was done
        Hours are decimal, rounded to the nearest 0.25; leave out entries under 0.25 h. Put general email, chat and
        browsing that fit no project in one "Communication / admin" row per day. After the table add a line
        "**Total: X h**". Use only what the log shows.
        {AiService.LanguageRule} {MarkdownOnly}

        {context}
        """;

    // ---- notes -----------------------------------------------------------------------

    /// <summary>Rewrites a selection (or a whole note) and returns only the new text.</summary>
    public static string Rewrite(string action, string text, bool wholeNote)
    {
        var task = action switch
        {
            "shorten" => "Make this text shorter and tighter (about half the length) while keeping every important fact.",
            "fix" => "Fix spelling, grammar and punctuation. Change nothing else — keep the wording, tone and structure.",
            "improve" => "Improve the writing: clearer, better flow, more concise. Keep the meaning, facts and tone.",
            _ when action.StartsWith("translate:") => $"Translate this text into {action["translate:".Length..]}. Keep names, code and URLs as they are.",
            _ => "Improve the writing.",
        };
        var keep = wholeNote
            ? "Keep the Markdown structure (headings, lists, quotes, code blocks, tables)."
            : "Keep inline Markdown formatting (**bold**, *italic*, `code`, [links](url)).";
        var language = action.StartsWith("translate:") ? "" : "Answer in the same language as the text.";
        return $"""
            {task}
            {keep} {language}
            Output only the rewritten text — no preamble, no quotes, no explanation, no ``` fence around it.

            Text:
            {text}
            """;
    }

    public static string Continue(string note, string lastBlock) => $"""
        Continue writing this note from where the marked paragraph ends: one to three short paragraphs (or more list
        items, if it ends in a list) that follow naturally in the same voice and language. Don't repeat what is there.
        Output only the new text in Markdown — no preamble, no ``` fence.

        The note:
        {note}

        Continue after this paragraph:
        {lastBlock}
        """;

    public static string Summarize(string note) => $"""
        Summarize this note in 3–7 Markdown bullet points ("- "), most important first. Keep names, numbers and dates.
        {AiService.LanguageRule} Output only the bullets.

        {note}
        """;

    public static string Todos(string note) => $"""
        Extract every action item / to-do from this note as a Markdown bullet list ("- "). Start each with a verb; add
        the owner and due date in parentheses when the note mentions them. If there are none, reply exactly: NONE
        {AiService.LanguageRule} Output only the list.

        {note}
        """;

    public static string Minutes(string note, string title) => $"""
        Turn these raw meeting notes into clean minutes in Markdown. Lines may start with a time stamp; use them for
        the order of events only. Use these sections (skip one if there is nothing for it):
        ## Summary — 2–4 sentences
        ## Decisions — bullets
        ## Action items — bullets "- **Owner** — task (due date)", owner "?" when unknown
        ## Discussion — the main points, short bullets
        Keep facts, names and numbers exactly; don't invent anything.
        {AiService.LanguageRule} {MarkdownOnly}

        Meeting: {title}

        {note}
        """;

    public static string TitleAndTags(string note) => $$"""
        Suggest a short title (at most 8 words) and 1–4 lowercase single-word tags for this note.
        Use the note's language for the title. Reply with JSON only: {"title": "...", "tags": ["...", "..."]}

        {{note}}
        """;

    /// <summary>Reads {"title", "tags"} from the answer; tags come back without "#".</summary>
    public static bool TryParseTitle(string answer, out string title, out List<string> tags)
    {
        title = "";
        tags = new();
        if (AiService.ExtractJson(answer) is not { } json) return false;
        title = AiService.Str(json, "title").Trim().Trim('"', '#', ' ');
        if (json.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            tags = t.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (x.GetString() ?? "").Trim().TrimStart('#').Replace(' ', '-').ToLowerInvariant())
                .Where(x => x.Length > 0)
                .Distinct()
                .Take(5)
                .ToList();
        return title.Length > 0;
    }

    // ---- scripts ---------------------------------------------------------------------

    public static string ExplainScript(string path, string interpreter, string arguments, string content, bool truncated) => $"""
        Explain this script to the person who is about to run it from a launcher. In Markdown:
        ## What it does — 2–4 sentences in plain language
        ## How to run it — interpreter, parameters / arguments it reads, environment variables, files it expects
        ## What it changes — files, folders, registry, network calls, processes it starts or stops, data it deletes
        ## Watch out — anything risky, destructive, irreversible or suspicious (or "Nothing notable.")
        Be concrete and brief; quote names from the code.
        {AiService.LanguageRule} {MarkdownOnly}

        File: {path}
        Launched with: {interpreter}{(arguments.Length > 0 ? " " + arguments : "")}
        {(truncated ? "(The file is long; only its beginning is shown.)" : "")}

        ```
        {content}
        ```
        """;

    public static string GenerateScript(string description, string language) => $"""
        Write a complete, ready-to-run {language} script for {(OperatingSystem.IsWindows() ? "Windows" : "macOS")} that does this:
        {description}

        Requirements: start with a comment that says in one line what the script does and how to run it; read
        settings from parameters with sensible defaults instead of hard-coding personal paths; print what it is doing;
        ask for confirmation or support a dry run before deleting or overwriting anything; handle errors with a clear message.
        Reply in exactly this format and nothing else:
        FILENAME: <a short kebab-case file name with the right extension>
        ```
        <the script>
        ```
        """;

    // ---- timers ----------------------------------------------------------------------

    public static string CreateTimer(string description) => $$"""
        Turn this request into an interval timer for a desktop app: "{{description}}"

        A timer fires every N minutes and then does one action:
        - "beep": beeps and shows a pop-up message
        - "command": runs a shell command ({{(OperatingSystem.IsWindows() ? "cmd.exe on Windows" : "/bin/sh")}})
        - "ai": sends a prompt to an AI and shows the answer. The prompt may use these placeholders, filled in when it fires:
          {{string.Join(", ", AiService.Placeholders.Select(p => p.Name + " (" + p.Description + ")"))}}

        Reply with JSON only, no other text:
        {"name": "short name", "intervalMinutes": 30, "action": "beep|command|ai", "command": "", "prompt": "", "message": "pop-up text", "notify": true}
        """;
}
