using System;
using System.IO;

namespace BrailleSoundDemo
{
    /// <summary>Immutable owning route/run, separate from camera and conversation sessions.</summary>
    public sealed class ExperienceIdentity
    {
        public readonly string WorkspaceId, ExperienceSessionId;
        public bool IsScoped => WorkspaceId.Length != 0;
        public string Key => WorkspaceId + ":" + ExperienceSessionId;
        public ExperienceIdentity(string workspaceId = null, string experienceSessionId = null)
        {
            WorkspaceId = workspaceId ?? ""; ExperienceSessionId = experienceSessionId ?? "";
            if (WorkspaceId.Length == 0 && ExperienceSessionId.Length == 0) return;
            if ((WorkspaceId != "judy" && WorkspaceId != "demo") ||
                !Guid.TryParseExact(ExperienceSessionId, "N", out var parsed) || parsed.ToString("N") != ExperienceSessionId)
                throw new ArgumentException("An experience requires judy/demo and a UUID session identity.");
        }
        public bool Matches(string workspaceId, string experienceSessionId) =>
            WorkspaceId == (workspaceId ?? "") && ExperienceSessionId == (experienceSessionId ?? "");
        public bool Matches(ExperienceIdentity other) => other != null && Matches(other.WorkspaceId, other.ExperienceSessionId);
        public string Query => IsScoped ? "&workspaceId=" + Uri.EscapeDataString(WorkspaceId) +
            "&experienceSessionId=" + Uri.EscapeDataString(ExperienceSessionId) : "";
        public string DirectoryUnder(string root) => IsScoped
            ? Path.Combine(root, "ExperienceSessions", WorkspaceId, ExperienceSessionId) : root;
    }
}
