using System;
using System.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using UnityEditor;

namespace MCPForUnity.Editor.Tests.EditMode.Services
{
    [TestFixture]
    public class ToolDiscoveryServiceTests
    {
        private const string TestToolName = "test_tool_for_testing";

        [SetUp]
        public void SetUp()
        {
            // Clean up any test preferences
            string testKey = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            if (EditorPrefs.HasKey(testKey))
            {
                EditorPrefs.DeleteKey(testKey);
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test preferences after each test
            string testKey = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            if (EditorPrefs.HasKey(testKey))
            {
                EditorPrefs.DeleteKey(testKey);
            }
        }

        [Test]
        public void SetToolEnabled_WritesToEditorPrefs()
        {
            // Arrange
            var service = new ToolDiscoveryService();

            // Act
            service.SetToolEnabled(TestToolName, false);

            // Assert
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            Assert.IsTrue(EditorPrefs.HasKey(key), "Preference key should exist after SetToolEnabled");
            Assert.IsFalse(EditorPrefs.GetBool(key, true), "Preference should be set to false");
        }

        [Test]
        public void IsToolEnabled_ReturnsFalse_WhenToolDoesNotExist()
        {
            // Arrange - Ensure no preference exists
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            if (EditorPrefs.HasKey(key))
            {
                EditorPrefs.DeleteKey(key);
            }

            var service = new ToolDiscoveryService();

            // Act - For a non-existent tool, IsToolEnabled should return false
            // (since metadata.AutoRegister defaults to false for non-existent tools)
            bool result = service.IsToolEnabled(TestToolName);

            // Assert - Non-existent tools return false (no metadata found)
            Assert.IsFalse(result, "Non-existent tool should return false");
        }

        [Test]
        public void IsToolEnabled_ReturnsStoredValue_WhenPreferenceExists()
        {
            // Arrange
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            EditorPrefs.SetBool(key, false);  // Store false value
            var service = new ToolDiscoveryService();

            // Act
            bool result = service.IsToolEnabled(TestToolName);

            // Assert
            Assert.IsFalse(result, "Should return the stored preference value (false)");
        }

        [Test]
        public void IsToolEnabled_ReturnsTrue_WhenPreferenceSetToTrue()
        {
            // Arrange
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            EditorPrefs.SetBool(key, true);
            var service = new ToolDiscoveryService();

            // Act
            bool result = service.IsToolEnabled(TestToolName);

            // Assert
            Assert.IsTrue(result, "Should return the stored preference value (true)");
        }

        [Test]
        public void ToolToggle_PersistsAcrossServiceInstances()
        {
            // Arrange
            var service1 = new ToolDiscoveryService();
            service1.SetToolEnabled(TestToolName, false);

            // Act - Create a new service instance
            var service2 = new ToolDiscoveryService();
            bool result = service2.IsToolEnabled(TestToolName);

            // Assert - The disabled state should persist
            Assert.IsFalse(result, "Tool state should persist across service instances");
        }

        [Test]
        public void ComputeDefaultEnabled_FollowsGroupDefaults()
        {
            // Built-in tools follow their group's default...
            Assert.IsTrue(ToolDiscoveryService.ComputeDefaultEnabled(
                new ToolMetadata { Name = "t", IsBuiltIn = true, Group = "core", AutoRegister = false }));
            Assert.IsFalse(ToolDiscoveryService.ComputeDefaultEnabled(
                new ToolMetadata { Name = "t", IsBuiltIn = true, Group = "vfx", AutoRegister = false }));
            // ...and built-in status alone must not imply enabled (the original bug).
            Assert.IsFalse(ToolDiscoveryService.ComputeDefaultEnabled(
                new ToolMetadata { Name = "t", IsBuiltIn = true, Group = "docs", AutoRegister = true }));

            // Custom project tools keep their attribute-driven registration behaviour.
            Assert.IsTrue(ToolDiscoveryService.ComputeDefaultEnabled(
                new ToolMetadata { Name = "t", IsBuiltIn = false, Group = "vfx", AutoRegister = true }));
            Assert.IsFalse(ToolDiscoveryService.ComputeDefaultEnabled(
                new ToolMetadata { Name = "t", IsBuiltIn = false, Group = "core", AutoRegister = false }));

            Assert.IsFalse(ToolDiscoveryService.ComputeDefaultEnabled(null));
        }

        [Test]
        public void FreshPreferences_UseGroupDefaults()
        {
            var service = new ToolDiscoveryService();
            var coreTool = service.DiscoverAllTools()
                .FirstOrDefault(t => t.IsBuiltIn && string.Equals(t.Group ?? "core", "core", StringComparison.OrdinalIgnoreCase));
            var optionalTool = service.DiscoverAllTools()
                .FirstOrDefault(t => t.IsBuiltIn && !string.Equals(t.Group ?? "core", "core", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(coreTool, "Expected at least one built-in core tool.");
            Assert.IsNotNull(optionalTool, "Expected at least one built-in optional-group tool.");

            var coreSnapshot = SnapshotPrefs(coreTool.Name);
            var optionalSnapshot = SnapshotPrefs(optionalTool.Name);
            try
            {
                DeletePrefs(coreTool.Name);
                DeletePrefs(optionalTool.Name);
                service.InvalidateCache();
                service.DiscoverAllTools();

                Assert.IsTrue(service.IsToolEnabled(coreTool.Name),
                    $"Core tool '{coreTool.Name}' should default to enabled.");
                Assert.IsFalse(service.IsToolEnabled(optionalTool.Name),
                    $"Optional-group tool '{optionalTool.Name}' ({optionalTool.Group}) should default to disabled.");
                Assert.IsFalse(service.IsToolExplicitlyDisabled(optionalTool.Name),
                    "Default-off must not read as an explicit disable — the tool stays executable for session activation.");
            }
            finally
            {
                RestorePrefs(coreTool.Name, coreSnapshot);
                RestorePrefs(optionalTool.Name, optionalSnapshot);
                service.InvalidateCache();
            }
        }

        [Test]
        public void LegacyEnabledPreference_DoesNotReenableOptionalGroupTool()
        {
            var service = new ToolDiscoveryService();
            var optionalTool = service.DiscoverAllTools()
                .FirstOrDefault(t => t.IsBuiltIn && !string.Equals(t.Group ?? "core", "core", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(optionalTool, "Expected at least one built-in optional-group tool.");

            var snapshot = SnapshotPrefs(optionalTool.Name);
            string legacyKey = EditorPrefKeys.LegacyToolEnabledPrefix + optionalTool.Name;
            try
            {
                DeletePrefs(optionalTool.Name);
                // v1 auto-initialised every built-in tool to true; this must not
                // survive migration as an "explicit enable".
                EditorPrefs.SetBool(legacyKey, true);
                service.InvalidateCache();
                service.DiscoverAllTools();

                Assert.IsFalse(service.IsToolEnabled(optionalTool.Name),
                    $"Legacy enabled default for '{optionalTool.Name}' must not re-enable the tool.");
                Assert.IsFalse(EditorPrefs.HasKey(legacyKey), "Legacy key should be deleted after migration.");
            }
            finally
            {
                RestorePrefs(optionalTool.Name, snapshot);
                service.InvalidateCache();
            }
        }

        [Test]
        public void LegacyDisabledPreference_IsMigratedAsExplicitDisable()
        {
            var service = new ToolDiscoveryService();
            var coreTool = service.DiscoverAllTools()
                .FirstOrDefault(t => t.IsBuiltIn && string.Equals(t.Group ?? "core", "core", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(coreTool, "Expected at least one built-in core tool.");

            var snapshot = SnapshotPrefs(coreTool.Name);
            string legacyKey = EditorPrefKeys.LegacyToolEnabledPrefix + coreTool.Name;
            try
            {
                DeletePrefs(coreTool.Name);
                // A stored false under v1 was always an explicit user choice.
                EditorPrefs.SetBool(legacyKey, false);
                service.InvalidateCache();
                service.DiscoverAllTools();

                Assert.IsFalse(service.IsToolEnabled(coreTool.Name),
                    $"Explicit v1 disable of '{coreTool.Name}' should be preserved by migration.");
                Assert.IsTrue(service.IsToolExplicitlyDisabled(coreTool.Name),
                    "A migrated v1 disable is an explicit user choice and should block execution.");
                Assert.IsFalse(EditorPrefs.HasKey(legacyKey), "Legacy key should be deleted after migration.");
            }
            finally
            {
                RestorePrefs(coreTool.Name, snapshot);
                service.InvalidateCache();
            }
        }

        [Test]
        public void ExplicitOptionalGroupEnable_PersistsAcrossDiscovery()
        {
            var service = new ToolDiscoveryService();
            var optionalTool = service.DiscoverAllTools()
                .FirstOrDefault(t => t.IsBuiltIn && !string.Equals(t.Group ?? "core", "core", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(optionalTool, "Expected at least one built-in optional-group tool.");

            var snapshot = SnapshotPrefs(optionalTool.Name);
            try
            {
                DeletePrefs(optionalTool.Name);
                service.InvalidateCache();
                service.DiscoverAllTools();

                service.SetToolEnabled(optionalTool.Name, true);
                service.InvalidateCache();
                service.DiscoverAllTools();

                Assert.IsTrue(service.IsToolEnabled(optionalTool.Name),
                    $"Explicit enable of '{optionalTool.Name}' should persist across rediscovery.");
            }
            finally
            {
                RestorePrefs(optionalTool.Name, snapshot);
                service.InvalidateCache();
            }
        }

        [Test]
        public void IsToolExplicitlyDisabled_TracksPersistedUserChoice()
        {
            var service = new ToolDiscoveryService();

            Assert.IsFalse(service.IsToolExplicitlyDisabled(TestToolName),
                "No stored preference means no explicit disable.");

            service.SetToolEnabled(TestToolName, false);
            Assert.IsTrue(service.IsToolExplicitlyDisabled(TestToolName));

            service.SetToolEnabled(TestToolName, true);
            Assert.IsFalse(service.IsToolExplicitlyDisabled(TestToolName));
        }

        private static (bool hadKey, bool value, bool hadLegacyKey, bool legacyValue) SnapshotPrefs(string toolName)
        {
            string key = EditorPrefKeys.ToolEnabledPrefix + toolName;
            string legacyKey = EditorPrefKeys.LegacyToolEnabledPrefix + toolName;
            return (
                EditorPrefs.HasKey(key),
                EditorPrefs.GetBool(key, false),
                EditorPrefs.HasKey(legacyKey),
                EditorPrefs.GetBool(legacyKey, false)
            );
        }

        private static void DeletePrefs(string toolName)
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.ToolEnabledPrefix + toolName);
            EditorPrefs.DeleteKey(EditorPrefKeys.LegacyToolEnabledPrefix + toolName);
        }

        private static void RestorePrefs(string toolName, (bool hadKey, bool value, bool hadLegacyKey, bool legacyValue) snapshot)
        {
            string key = EditorPrefKeys.ToolEnabledPrefix + toolName;
            string legacyKey = EditorPrefKeys.LegacyToolEnabledPrefix + toolName;
            if (snapshot.hadKey) EditorPrefs.SetBool(key, snapshot.value); else EditorPrefs.DeleteKey(key);
            if (snapshot.hadLegacyKey) EditorPrefs.SetBool(legacyKey, snapshot.legacyValue); else EditorPrefs.DeleteKey(legacyKey);
        }

        [Test]
        public void DiscoverAllTools_DoesNotOverrideStoredFalse_ForBuiltInAutoRegisterFalseTool()
        {
            // Arrange
            var service = new ToolDiscoveryService();
            var builtInTool = service.DiscoverAllTools()
                .FirstOrDefault(tool => tool.IsBuiltIn && !tool.AutoRegister);

            Assert.IsNotNull(builtInTool, "Expected at least one built-in tool with AutoRegister=false.");

            string key = EditorPrefKeys.ToolEnabledPrefix + builtInTool.Name;
            bool hadOriginalKey = EditorPrefs.HasKey(key);
            bool originalValue = hadOriginalKey && EditorPrefs.GetBool(key, true);

            try
            {
                EditorPrefs.SetBool(key, false);
                service.InvalidateCache();

                // Act
                service.DiscoverAllTools();
                bool enabled = service.IsToolEnabled(builtInTool.Name);

                // Assert
                Assert.IsFalse(enabled, $"Built-in tool '{builtInTool.Name}' should remain disabled when preference is false.");
            }
            finally
            {
                if (hadOriginalKey)
                {
                    EditorPrefs.SetBool(key, originalValue);
                }
                else
                {
                    EditorPrefs.DeleteKey(key);
                }
            }
        }
    }
}
