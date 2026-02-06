using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Setup;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.MenuItems
{
    public static class MCPForUnityMenu
    {
        [MenuItem(ProductInfo.MenuRoot + "/Toggle MCP Window %#m", priority = 1)]
        public static void ToggleMCPWindow()
        {
            MCPForUnityEditorWindow.ShowWindow();
        }

        [MenuItem(ProductInfo.MenuRoot + "/Local Setup Window", priority = 2)]
        public static void ShowSetupWindow()
        {
            SetupWindowService.ShowSetupWindow();
        }


        [MenuItem(ProductInfo.MenuRoot + "/Edit EditorPrefs", priority = 3)]
        public static void ShowEditorPrefsWindow()
        {
            EditorPrefsWindow.ShowWindow();
        }

        // Screenshot sizing helpers. This controls a downscale step inside manage_scene screenshot.
        [MenuItem("Window/MCP For Unity/Screenshots/Downscale 100% (Full)", priority = 50)]
        public static void SetScreenshotDownscaleFull()
        {
            EditorPrefs.SetFloat(EditorPrefKeys.ScreenshotDownscaleFactor, 1f);
            Debug.Log("[MCP For Unity] Screenshot downscale set to 1.0 (full size).");
        }

        [MenuItem("Window/MCP For Unity/Screenshots/Downscale 50% (Half)", priority = 51)]
        public static void SetScreenshotDownscaleHalf()
        {
            EditorPrefs.SetFloat(EditorPrefKeys.ScreenshotDownscaleFactor, 0.5f);
            Debug.Log("[MCP For Unity] Screenshot downscale set to 0.5 (half resolution).");
        }

        [MenuItem("Window/MCP For Unity/Screenshots/Downscale 25% (Quarter)", priority = 52)]
        public static void SetScreenshotDownscaleQuarter()
        {
            EditorPrefs.SetFloat(EditorPrefKeys.ScreenshotDownscaleFactor, 0.25f);
            Debug.Log("[MCP For Unity] Screenshot downscale set to 0.25 (quarter resolution).");
        }
    }
}
