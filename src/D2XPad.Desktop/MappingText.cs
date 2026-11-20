using D2XPad.Live;

namespace D2XPad.Desktop;

internal static class MappingText
{
    internal static readonly IReadOnlyDictionary<string, InputControl> Targets =
        PadBindings.ActionTargets.ToDictionary(target => target.Id, StringComparer.OrdinalIgnoreCase);

    internal static string Describe(IReadOnlyList<XboxAction> actions)
    {
        if (actions.Count == 0) return "Unassigned";
        return string.Join(" + ", actions.Select(action =>
        {
            var target = Targets[action.Target];
            return target.IsAnalog ? $"{target.Name} {action.Strength}%" : target.Name;
        }));
    }
}
