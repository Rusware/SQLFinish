using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SQLFinish.Engine;

namespace SQLFinish.Vs
{
    /// <summary>
    /// Tools > Options > SQLFinish. The shell draws the page from these properties and keeps their values
    /// with SSMS's other settings.
    /// </summary>
    // ponytail: switches only. Formatting style and custom snippets get a page of their own when they exist.
    [Guid("5d0c2c53-3f0e-4c5e-9d39-6f2a51b0c7e4")]
    public sealed class OptionsPage : DialogPage
    {
        int scriptsKept = 1000;

        [Category("Completion"), DisplayName("Blank line ends a statement")]
        [Description("A statement left without its \";\" ends at the next blank line, so the query typed after it gets suggestions of its own. Off: only \";\" and GO end a statement.")]
        public bool BlankLineEndsStatement
        {
            get => Tokenizer.BlankLineEndsStatement;
            set => Tokenizer.BlankLineEndsStatement = value;
        }

        [Category("Execute guard"), DisplayName("Ask when there is no WHERE")]
        [Description("Executing a script with an UPDATE or DELETE that has no WHERE clause asks first.")]
        public bool ExecuteGuard { get; set; } = true;

        [Category("Query history"), DisplayName("Remember executed scripts")]
        [Description("Keeps every script you execute for the Query History window. Off: nothing new is recorded; what is already there stays.")]
        public bool RememberScripts { get; set; } = true;

        [Category("Query history"), DisplayName("Scripts kept")]
        [Description("The oldest scripts are deleted once the history holds more than this many. Pinned scripts are kept regardless, and not counted.")]
        public int ScriptsKept
        {
            get => scriptsKept;
            set => scriptsKept = Math.Max(1, value);
        }

        [Category("Export to Excel"), DisplayName("Ask where to save")]
        [Description("Off: the results open in Excel straight away, as a temporary workbook that you save from Excel if you want to keep it. On: you choose the file first.")]
        public bool AskWhereToSave { get; set; }

        [Category("Query windows"), DisplayName("Reopen query windows")]
        [Description("Starting SSMS brings back the query windows that were open when it was last closed, with their text (saved or not) and their connection. Off: nothing is kept.")]
        public bool ReopenWindows { get; set; } = true;
    }
}
