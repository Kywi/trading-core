using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GripTrader.Tuner.Sweep
{
    /// <summary>
    /// Expands a <see cref="SweepConfig"/> into the full cartesian product of
    /// parameter combinations. Order is deterministic: parameter keys are
    /// iterated in insertion order, and the rightmost dimension varies
    /// fastest (so the output reads naturally as a nested-loop expansion).
    /// Values are boxed as <see cref="object"/> so a single combination can
    /// mix decimals, ints, bools and strings.
    /// </summary>
    public static class SweepExpander
    {
        /// <summary>Turn a <see cref="ParameterSpec"/> into the concrete list of values it represents.</summary>
        public static List<object> ExpandSpec(string paramName, ParameterSpec spec)
        {
            // Count how many of the four mutually-exclusive value sources are
            // populated. Exactly one must be set per parameter.
            int sources = 0;
            if (spec.Range != null) sources++;
            if (spec.Values != null) sources++;
            if (spec.BoolValues != null) sources++;
            if (spec.StringValues != null) sources++;

            if (sources != 1)
                throw new InvalidDataException(
                    $"Parameter '{paramName}': specify exactly one of 'Range' (with 'Step'), 'Values', 'BoolValues', or 'StringValues'.");

            if (spec.Range != null)
            {
                if (spec.Range.Length != 2)
                    throw new InvalidDataException($"Parameter '{paramName}': 'Range' must be a [min, max] pair.");
                if (!spec.Step.HasValue || spec.Step.Value <= 0m)
                    throw new InvalidDataException($"Parameter '{paramName}': 'Step' must be a positive decimal when using 'Range'.");

                var min = spec.Range[0];
                var max = spec.Range[1];
                if (max < min)
                    throw new InvalidDataException($"Parameter '{paramName}': Range max ({max}) is less than min ({min}).");

                var step = spec.Step.Value;
                var values = new List<object>();
                // Half-step epsilon guards against decimal arithmetic that
                // overshoots max by a fraction of a step (rare with decimal,
                // but cheap insurance).
                for (var v = min; v <= max + step / 2m; v += step)
                    values.Add(v);
                return values;
            }

            if (spec.Values != null)
            {
                if (spec.Values.Length == 0)
                    throw new InvalidDataException($"Parameter '{paramName}': 'Values' must not be empty.");
                return spec.Values.Select(v => (object)v).ToList();
            }

            if (spec.BoolValues != null)
            {
                if (spec.BoolValues.Length == 0)
                    throw new InvalidDataException($"Parameter '{paramName}': 'BoolValues' must not be empty.");
                return spec.BoolValues.Select(v => (object)v).ToList();
            }

            // StringValues
            if (spec.StringValues!.Length == 0)
                throw new InvalidDataException($"Parameter '{paramName}': 'StringValues' must not be empty.");
            return spec.StringValues.Select(v => (object)v).ToList();
        }

        /// <summary>
        /// Cartesian product. Returns one dictionary per combination, with
        /// the same keys as <paramref name="dimensions"/>.
        /// </summary>
        public static IEnumerable<Dictionary<string, object>> Cartesian(IDictionary<string, IList<object>> dimensions)
        {
            if (dimensions.Count == 0)
            {
                yield return new Dictionary<string, object>();
                yield break;
            }

            var keys = dimensions.Keys.ToList();
            var sizes = keys.Select(k => dimensions[k].Count).ToArray();
            if (sizes.Any(s => s == 0))
                yield break;

            var indices = new int[keys.Count];
            while (true)
            {
                var combo = new Dictionary<string, object>(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                    combo[keys[i]] = dimensions[keys[i]][indices[i]];
                yield return combo;

                // Increment from the rightmost dimension; carry leftward.
                int dim = keys.Count - 1;
                while (dim >= 0)
                {
                    indices[dim]++;
                    if (indices[dim] < sizes[dim]) break;
                    indices[dim] = 0;
                    dim--;
                }
                if (dim < 0) break;
            }
        }

        /// <summary>Convenience: full expansion as an in-memory list of (paramKeysInOrder, combinations).</summary>
        public static (List<string> ParamOrder, List<Dictionary<string, object>> Combos) Expand(SweepConfig config)
        {
            var dims = new Dictionary<string, IList<object>>();
            foreach (var kv in config.Parameters)
                dims[kv.Key] = ExpandSpec(kv.Key, kv.Value);

            return (new List<string>(config.Parameters.Keys), Cartesian(dims).ToList());
        }
    }
}
