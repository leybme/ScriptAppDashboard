using System;
using System.Collections.Generic;

namespace ScriptAppDashboard.Models;

public class ScriptItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string IconPath { get; set; } = "";

    /// <summary>auto | python | pwsh | bash | node | cmd | direct | custom</summary>
    public string Interpreter { get; set; } = "auto";
    public string CustomCommand { get; set; } = "";
    public bool KeepConsoleOpen { get; set; }
}

public class AppConfig
{
    public List<ScriptItem> Scripts { get; set; } = new();
}
