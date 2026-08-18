using TestCaseHub.Api.Dtos;

namespace TestCaseHub.Api.Services;

// Mirrors validateTestCasePayload() in the original artifact: Module/Task area/Verification
// type/Title are required, and every step must carry BOTH an action and an expected result
// (a step without both cannot be automated later, so it is rejected rather than half-saved).
public static class TestCaseValidation
{
    public static List<string> Validate(TestCaseCreateRequest req, bool moduleExists)
    {
        var missing = new List<string>();
        if (!moduleExists) missing.Add("Module");
        if (string.IsNullOrWhiteSpace(req.Layer)) missing.Add("Task area");
        if (string.IsNullOrWhiteSpace(req.VerificationType)) missing.Add("Verification type");
        if (string.IsNullOrWhiteSpace(req.Title)) missing.Add("Title");

        var attempted = (req.Steps ?? new())
            .Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.ExpectedResult))
            .ToList();

        if (attempted.Count == 0)
        {
            missing.Add("At least one step (action + expected result)");
        }
        else
        {
            for (int i = 0; i < attempted.Count; i++)
            {
                var s = attempted[i];
                if (string.IsNullOrWhiteSpace(s.Action) || string.IsNullOrWhiteSpace(s.ExpectedResult))
                {
                    missing.Add($"Step {i + 1} — needs both an action and an expected result (required so it can be automated later)");
                }
            }
        }
        return missing;
    }
}
