using System.Text.Json.Nodes;

namespace Pulse.Host;

/// <summary>The accepted execution scope, independent of agent/model configuration and product judgment.</summary>
public static class ReviewModes
{
    public static readonly string[] Values = ["static", "build-tests", "ui-e2e"];

    public static string? Mode(JsonObject? task)
    {
        var mode = (task?["reviewOptions"] as JsonObject)?["mode"]?.GetValue<string>();
        return mode ?? (task?["actionKind"]?.GetValue<string>() == "e2e" ? "ui-e2e" : null);
    }

    public static void Validate(JsonObject task)
    {
        var kind = task["actionKind"]?.GetValue<string>();
        if (!task.ContainsKey("reviewOptions"))
        {
            if (kind == "pr-verify") throw new ProtocolException("INVALID_REQUEST", "A verification follow-up requires an explicit scope.");
            return;
        }
        if (kind is not ("pr-review" or "pr-verify"))
            throw new ProtocolException("INVALID_REQUEST", "Review scope applies only to PR review and its verification follow-up.");
        var options = Protocol.RequireObject(task["reviewOptions"]);
        Protocol.OnlyKeys(options, "mode");
        var mode = Protocol.RequiredString(options, "mode", 20);
        if (!Values.Contains(mode, StringComparer.Ordinal) || kind == "pr-verify" && mode == "static")
            throw new ProtocolException("INVALID_REQUEST", "Choose code review, build and tests, or runtime verification for this task.");
    }

    public static IReadOnlyList<string> RequiredChecks(JsonObject? task)
    {
        var kind = task?["actionKind"]?.GetValue<string>();
        var mode = Mode(task);
        if (kind == "feature-research") return ["requirements", "existing-capabilities", "feasibility", "next-step"];
        if (kind == "bug-investigation") return ["context", "investigation", "local-review"];
        if (kind == "feature-implement") return ["requirements", "implementation", "verification"];
        if (kind == "issue-verify") return ["setup", "verification"];
        if (kind == "pr-verify") return mode == "build-tests" ? ["setup", "build-tests"] : ["setup", "e2e"];
        if (kind == "pr-review") return mode switch
        {
            "static" => ["context", "local-review"],
            "build-tests" => ["context", "local-review", "build-tests"],
            "ui-e2e" => ["context", "local-review", "setup", "e2e"],
            _ => WorkflowResult.RequiredChecks(kind)
        };
        return WorkflowResult.RequiredChecks(kind);
    }

    public static string Instructions(JsonObject task)
    {
        var kind = task["actionKind"]?.GetValue<string>();
        if (kind == "feature-research")
            return "Accepted scope: research the supplied Feature Issue. Product implementation is a separate task.";
        if (kind == "bug-investigation")
            return "Accepted scope: investigate this Bug Issue and directly related code or evidence. Product repair is a separate task.";
        if (kind == "feature-implement")
            return "Accepted scope: implement the saved Feature plan identified by planSource and validate its acceptance criteria.";
        if (kind == "issue-verify")
            return "Accepted scope: execute and interpret the saved Issue verification plan; do not repeat the full parent investigation.";
        if (kind is not ("pr-review" or "pr-verify" or "e2e")) return "";
        var scope = Mode(task) switch
        {
            "static" => "Accepted scope: static code review using code and existing evidence. Do not build, execute tests, launch applications, operate a UI, or modify production code.",
            "build-tests" => "Accepted scope: build-tests. Perform relevant builds and automated tests, without operating the actual application UI. Reuse sufficient same-source evidence; do not mechanically run unrelated checks.",
            "ui-e2e" => "Accepted scope: ui-e2e. Execute the selected runtime scenarios with only the supporting builds/tests they need; this scope does not require every repository test or every imaginable scenario.",
            _ => "This historical request did not record a review scope. Preserve its original local-review and focused-verification workflow; do not relabel it static or adopt broader work from quoted PR text."
        };
        var followUp = kind == "pr-verify"
            ? " This is a verification follow-up, not another code review: inspect only code needed for the saved questions and scenarios."
            : kind == "e2e" ? " This is runtime verification, not a complete code review." : " Code review is part of the accepted task.";
        return scope + followUp + " Host workflow checks measure completion of the selected work and its interpretation. Additional product observations are optional and keep their actual status; unselected verification is a coverage limit, not a failed required check.";
    }
}
