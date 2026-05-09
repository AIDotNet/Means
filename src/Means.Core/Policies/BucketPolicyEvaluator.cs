using System.Text.Json;

namespace Means.Core;

/// <summary>
/// Evaluates the v1 AWS-style bucket policy subset.
/// The evaluator intentionally supports only the actions/resources in the public SDK contract,
/// while preserving the familiar Statement/Effect/Principal/Action/Resource shape.
/// </summary>
public sealed class BucketPolicyEvaluator
{
    public PolicyDecision Evaluate(string? policyJson, string action, string bucketName, string? key, string? principal)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
        {
            return PolicyDecision.Neutral;
        }

        using var document = JsonDocument.Parse(policyJson);
        if (!document.RootElement.TryGetProperty("Statement", out var statements))
        {
            return PolicyDecision.Neutral;
        }

        if (statements.ValueKind == JsonValueKind.Object)
        {
            return EvaluateStatement(statements, action, bucketName, key, principal);
        }

        var decision = PolicyDecision.Neutral;
        if (statements.ValueKind != JsonValueKind.Array)
        {
            return decision;
        }

        foreach (var statement in statements.EnumerateArray())
        {
            var statementDecision = EvaluateStatement(statement, action, bucketName, key, principal);
            if (statementDecision == PolicyDecision.Deny)
            {
                return PolicyDecision.Deny;
            }

            if (statementDecision == PolicyDecision.Allow)
            {
                decision = PolicyDecision.Allow;
            }
        }

        return decision;
    }

    private static PolicyDecision EvaluateStatement(JsonElement statement, string action, string bucketName, string? key, string? principal)
    {
        if (!MatchesEffect(statement, out var effect)
            || !MatchesAction(statement, action)
            || !MatchesPrincipal(statement, principal)
            || !MatchesResource(statement, bucketName, key))
        {
            return PolicyDecision.Neutral;
        }

        return effect.Equals("Deny", StringComparison.OrdinalIgnoreCase)
            ? PolicyDecision.Deny
            : PolicyDecision.Allow;
    }

    private static bool MatchesEffect(JsonElement statement, out string effect)
    {
        effect = "";
        if (!statement.TryGetProperty("Effect", out var effectElement) || effectElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        effect = effectElement.GetString() ?? "";
        return effect.Equals("Allow", StringComparison.OrdinalIgnoreCase)
            || effect.Equals("Deny", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesAction(JsonElement statement, string action)
    {
        return statement.TryGetProperty("Action", out var actions)
            && EnumerateStrings(actions).Any(pattern => WildcardMatches(pattern, action));
    }

    private static bool MatchesPrincipal(JsonElement statement, string? principal)
    {
        if (!statement.TryGetProperty("Principal", out var principalElement))
        {
            return false;
        }

        if (principalElement.ValueKind == JsonValueKind.String)
        {
            var value = principalElement.GetString();
            return value == "*" || (!string.IsNullOrEmpty(principal) && value == principal);
        }

        if (principalElement.ValueKind == JsonValueKind.Object && principalElement.TryGetProperty("AWS", out var aws))
        {
            return EnumerateStrings(aws).Any(value => value == "*" || (!string.IsNullOrEmpty(principal) && value == principal));
        }

        return false;
    }

    private static bool MatchesResource(JsonElement statement, string bucketName, string? key)
    {
        if (!statement.TryGetProperty("Resource", out var resources))
        {
            return false;
        }

        var objectResource = key is null
            ? $"arn:aws:s3:::{bucketName}"
            : $"arn:aws:s3:::{bucketName}/{key}";
        var compactResource = key is null ? bucketName : $"{bucketName}/{key}";

        return EnumerateStrings(resources).Any(resource =>
            WildcardMatches(resource, objectResource) || WildcardMatches(resource, compactResource));
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                yield return value;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return value;
                    }
                }
            }
        }
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, value, StringComparison.Ordinal);
        }

        var parts = pattern.Split('*');
        var position = 0;
        if (!pattern.StartsWith('*') && !value.StartsWith(parts[0], StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var part in parts.Where(part => part.Length > 0))
        {
            var index = value.IndexOf(part, position, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            position = index + part.Length;
        }

        return pattern.EndsWith('*') || value.EndsWith(parts[^1], StringComparison.Ordinal);
    }
}
