namespace LlmodelBox.Runtime.Intent
{
    // What the runtime believes the user is trying to do this turn. Deterministic classification (no
    // extra model call) drives whether the runtime advertises tools, pulls project knowledge, or just
    // answers — so a "hi" doesn't index the repo and an "add a method" turn does.
    public enum IntentKind
    {
        Chat,        // greeting / small talk / acknowledgement — no tools, no knowledge
        Question,    // an informational question (may want project knowledge)
        ExplainCode, // asking to explain/understand existing code — wants knowledge
        EditCode,    // change the code (add/fix/refactor/rename/…) — wants tools + knowledge
        RunTool,     // run/execute/read/list — wants tools
        Search,      // find/locate something in the project — wants knowledge
        Unknown      // couldn't classify — stay permissive (tools + knowledge)
    }
}
