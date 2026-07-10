using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Services.MutationTransactions
{
    public sealed class MutationChangeGuard
    {
        private readonly HashSet<string> expectedObjects;
        private readonly IReadOnlyList<string> expectedProperties;

        private MutationChangeGuard(
            IEnumerable<string> objects,
            IEnumerable<string> properties,
            int? maxChangedObjects)
        {
            expectedObjects = new HashSet<string>(objects ?? Array.Empty<string>(), StringComparer.Ordinal);
            expectedProperties = (properties ?? Array.Empty<string>()).ToArray();
            MaxChangedObjects = maxChangedObjects;
        }

        public int? MaxChangedObjects { get; }

        public static bool TryParse(JObject parameters, out MutationChangeGuard guard, out ErrorResponse error)
        {
            guard = null;
            error = null;
            JToken token = parameters?["changeGuard"] ?? parameters?["change_guard"];
            if (token == null || token.Type == JTokenType.Null)
                return true;

            if (!(token is JObject value))
            {
                error = new ErrorResponse("INVALID_CHANGE_GUARD", new { message = "change_guard must be an object." });
                return false;
            }

            string mode = value["mode"]?.ToString() ?? "reject_unexpected";
            if (!string.Equals(mode, "reject_unexpected", StringComparison.Ordinal))
            {
                error = new ErrorResponse("INVALID_CHANGE_GUARD", new { message = "change_guard.mode must be 'reject_unexpected'." });
                return false;
            }

            string[] objects = ReadStrings(value["expected_objects"] ?? value["expectedObjects"]);
            string[] properties = ReadStrings(value["expected_properties"] ?? value["expectedProperties"]);
            int? maxChangedObjects = value["max_changed_objects"]?.ToObject<int?>()
                ?? value["maxChangedObjects"]?.ToObject<int?>();
            if (maxChangedObjects < 0)
            {
                error = new ErrorResponse("INVALID_CHANGE_GUARD", new { message = "max_changed_objects cannot be negative." });
                return false;
            }
            if (objects.Length == 0 && properties.Length == 0 && !maxChangedObjects.HasValue)
            {
                error = new ErrorResponse("INVALID_CHANGE_GUARD", new { message = "change_guard must declare an expected object, property, or maximum changed-object count." });
                return false;
            }

            guard = new MutationChangeGuard(objects, properties, maxChangedObjects);
            return true;
        }

        public IReadOnlyList<SerializedChange> Unexpected(IReadOnlyList<SerializedChange> changes)
        {
            bool overLimit = MaxChangedObjects.HasValue
                && changes.Select(change => change.ObjectId).Distinct().Count() > MaxChangedObjects.Value;
            if (overLimit)
                return changes;
            return changes.Where(change => !MatchesObject(change) && !MatchesProperty(change)).ToArray();
        }

        public object Execute(
            IEnumerable<Object> targets,
            Func<object> mutation,
            MutationTransactionOptions options = null)
        {
            return Execute(MutationTransaction.Begin(targets, options), mutation);
        }

        public object Execute(Scene scene, MutationTransactionOptions options, Func<object> mutation)
        {
            return Execute(MutationTransaction.BeginScene(scene, options), mutation);
        }

        private object Execute(MutationTransaction transaction, Func<object> mutation)
        {
            using (transaction)
            {
                object result = mutation();
                JObject response = JObject.FromObject(result);
                if (response["success"]?.Value<bool>() != true)
                {
                    transaction.Rollback();
                    return result;
                }

                IReadOnlyList<SerializedChange> changes = transaction.Changes;
                IReadOnlyList<SerializedChange> unexpected = Unexpected(changes);
                if (unexpected.Count > 0)
                {
                    transaction.Rollback();
                    return new ErrorResponse("UNEXPECTED_SERIALIZED_CHANGES", new
                    {
                        committed = false,
                        rolled_back = true,
                        changes,
                        unexpected_changes = unexpected
                    });
                }

                transaction.Commit(save: false);
                JObject data = response["data"] as JObject ?? new JObject();
                data["change_guard"] = JObject.FromObject(new
                {
                    committed = true,
                    rolled_back = false,
                    changed_objects = changes.Select(change => change.ObjectId).Distinct().Count(),
                    changes
                });
                response["data"] = data;
                return response;
            }
        }

        private bool MatchesObject(SerializedChange change)
        {
            return expectedObjects.Any(expected =>
                string.Equals(change.ObjectId, expected, StringComparison.Ordinal)
                || string.Equals(change.ObjectPath, expected, StringComparison.Ordinal)
                || change.ObjectPath.EndsWith("/" + expected, StringComparison.Ordinal));
        }

        private bool MatchesProperty(SerializedChange change)
        {
            string component = change.ComponentType?.Split('.').Last() ?? string.Empty;
            string objectName = change.ObjectPath?.Split('/').Last() ?? string.Empty;
            string[] actualScopes =
            {
                change.Property,
                component + "." + change.Property,
                change.ObjectPath + "." + change.Property,
                objectName + "." + change.Property,
                change.ObjectPath + "." + component + "." + change.Property,
                objectName + "." + component + "." + change.Property
            };
            return expectedProperties.Any(expected => actualScopes.Any(actual =>
                string.Equals(actual, expected, StringComparison.Ordinal)
                || actual.StartsWith(expected + ".", StringComparison.Ordinal)));
        }

        private static string[] ReadStrings(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return Array.Empty<string>();
            if (token.Type == JTokenType.String)
                return new[] { token.ToString() };
            return token.Type == JTokenType.Array
                ? token.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : Array.Empty<string>();
        }
    }
}
