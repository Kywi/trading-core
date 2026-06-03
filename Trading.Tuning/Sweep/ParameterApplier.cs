using System;
using System.Collections.Generic;
using System.IO;
using GripTrader.Core.Settings;

namespace GripTrader.Tuner.Sweep
{
    /// <summary>
    /// Applies a sweep parameter combination to a <see cref="BotSettings"/>
    /// instance via reflection. Supports decimal, int, bool and string
    /// targets — values are dispatched on the target property type rather
    /// than on the source object's runtime type, so JSON-side type
    /// inference (e.g. JSON <c>0.001</c> → <c>double</c>) doesn't matter.
    /// </summary>
    public static class ParameterApplier
    {
        public static void Apply(TradingSettingsBase settings, IDictionary<string, object> values)
        {
            // Reflect over the runtime settings type so inherited TradingSettingsBase
            // fields (and any future strategy's derived settings) all bind by flat name.
            var type = settings.GetType();
            foreach (var kv in values)
            {
                var prop = type.GetProperty(kv.Key)
                    ?? throw new InvalidDataException(
                        $"Sweep parameter '{kv.Key}' does not match any settings property. Check the JSON parameter name (case-sensitive).");

                if (!prop.CanWrite)
                    throw new InvalidDataException($"Sweep parameter '{kv.Key}' is read-only on the settings type.");

                var target = prop.PropertyType;
                var raw = kv.Value;

                try
                {
                    if (target == typeof(decimal))
                        prop.SetValue(settings, Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture));
                    else if (target == typeof(int))
                    {
                        var d = Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture);
                        if (decimal.Truncate(d) != d)
                            throw new InvalidDataException(
                                $"Sweep parameter '{kv.Key}' targets an int but the value '{raw}' has a fractional component; provide a whole number.");
                        prop.SetValue(settings, (int)d);
                    }
                    else if (target == typeof(double))
                        prop.SetValue(settings, Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture));
                    else if (target == typeof(bool))
                        prop.SetValue(settings, Convert.ToBoolean(raw));
                    else if (target == typeof(string))
                        prop.SetValue(settings, raw?.ToString() ?? "");
                    else
                        throw new InvalidDataException(
                            $"Sweep parameter '{kv.Key}' has unsupported target type {target.Name}. " +
                            $"Supported: decimal, int, double, bool, string.");
                }
                catch (FormatException ex)
                {
                    throw new InvalidDataException(
                        $"Sweep parameter '{kv.Key}': cannot convert value '{raw}' (type {raw?.GetType().Name ?? "null"}) to {target.Name}.", ex);
                }
                catch (InvalidCastException ex)
                {
                    throw new InvalidDataException(
                        $"Sweep parameter '{kv.Key}': cannot convert value '{raw}' (type {raw?.GetType().Name ?? "null"}) to {target.Name}.", ex);
                }
            }
        }
    }
}
