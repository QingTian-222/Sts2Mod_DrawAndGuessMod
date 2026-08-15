using System.Globalization;
using System.Reflection;

namespace DrawAndGuessMod.Scripts.Compatibility;

internal static class RunRngCompat
{
    public static ulong GetSeed(object rngSet)
    {
        PropertyInfo? seedProperty = rngSet.GetType().GetProperty(
            "Seed",
            BindingFlags.Instance | BindingFlags.Public);
        object? value = seedProperty?.GetValue(rngSet);
        if (value == null)
        {
            throw new MissingMemberException(rngSet.GetType().FullName, "Seed");
        }

        return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
    }
}
