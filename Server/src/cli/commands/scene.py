"""Scene CLI commands."""

import sys
import click
from typing import Optional, Any

from cli.utils.config import get_config
from cli.utils.output import format_output, print_error, print_success
from cli.utils.connection import run_command, handle_unity_errors


@click.group()
def scene():
    """Scene operations - hierarchy, load, save, create scenes."""
    pass


@scene.command("hierarchy")
@click.option(
    "--parent",
    default=None,
    help="Parent GameObject to list children of (name, path, or instance ID)."
)
@click.option(
    "--max-depth", "-d",
    default=None,
    type=int,
    help="Maximum depth to traverse."
)
@click.option(
    "--include-transform", "-t",
    is_flag=True,
    help="Include transform data for each node."
)
@click.option(
    "--limit", "-l",
    default=50,
    type=int,
    help="Maximum nodes to return."
)
@click.option(
    "--cursor", "-c",
    default=0,
    type=int,
    help="Pagination cursor."
)
@handle_unity_errors
def hierarchy(
    parent: Optional[str],
    max_depth: Optional[int],
    include_transform: bool,
    limit: int,
    cursor: int,
):
    """Get the scene hierarchy.

    \b
    Examples:
        unity-mcp scene hierarchy
        unity-mcp scene hierarchy --max-depth 3
        unity-mcp scene hierarchy --parent "Canvas" --include-transform
        unity-mcp scene hierarchy --format json
    """
    config = get_config()

    params: dict[str, Any] = {
        "action": "get_hierarchy",
        "pageSize": limit,
        "cursor": cursor,
    }

    if parent:
        params["parent"] = parent
    if max_depth is not None:
        params["maxDepth"] = max_depth
    if include_transform:
        params["includeTransform"] = True

    result = run_command("manage_scene", params, config)
    click.echo(format_output(result, config.format))


@scene.command("active")
@handle_unity_errors
def active():
    """Get information about the active scene."""
    config = get_config()
    result = run_command("manage_scene", {"action": "get_active"}, config)
    click.echo(format_output(result, config.format))


@scene.command("load")
@click.argument("scene")
@click.option(
    "--by-index", "-i",
    is_flag=True,
    help="Load by build index instead of path/name."
)
@handle_unity_errors
def load(scene: str, by_index: bool):
    """Load a scene.

    \b
    Examples:
        unity-mcp scene load "Assets/Scenes/Main.unity"
        unity-mcp scene load "MainScene"
        unity-mcp scene load 0 --by-index
    """
    config = get_config()

    params: dict[str, Any] = {"action": "load"}

    if by_index:
        try:
            params["buildIndex"] = int(scene)
        except ValueError:
            print_error(f"Invalid build index: {scene}")
            sys.exit(1)
    else:
        if scene.endswith(".unity"):
            params["path"] = scene
        else:
            params["name"] = scene

    result = run_command("manage_scene", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Loaded scene: {scene}")


@scene.command("save")
@click.option(
    "--path",
    default=None,
    help="Path to save the scene to (for new scenes)."
)
@handle_unity_errors
def save(path: Optional[str]):
    """Save the current scene.

    \b
    Examples:
        unity-mcp scene save
        unity-mcp scene save --path "Assets/Scenes/NewScene.unity"
    """
    config = get_config()

    params: dict[str, Any] = {"action": "save"}
    if path:
        params["path"] = path

    result = run_command("manage_scene", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Scene saved")


@scene.command("create")
@click.argument("name")
@click.option(
    "--path",
    default=None,
    help="Path to create the scene at."
)
@handle_unity_errors
def create(name: str, path: Optional[str]):
    """Create a new scene.

    \b
    Examples:
        unity-mcp scene create "NewLevel"
        unity-mcp scene create "TestScene" --path "Assets/Scenes/Test"
    """
    config = get_config()

    params: dict[str, Any] = {
        "action": "create",
        "name": name,
    }
    if path:
        params["path"] = path

    result = run_command("manage_scene", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success(f"Created scene: {name}")


@scene.command("build-settings")
@handle_unity_errors
def build_settings():
    """Get scenes in build settings."""
    config = get_config()
    result = run_command("manage_scene", {"action": "get_build_settings"}, config)
    click.echo(format_output(result, config.format))


@scene.command("screenshot")
@click.option(
    "--filename", "-f",
    default=None,
    help="Output filename (default: timestamp)."
)
@click.option(
    "--supersize", "-s",
    default=1,
    type=int,
    help="Supersize multiplier (1-4)."
)
@click.option(
    "--with-preview",
    is_flag=True,
    help="Return a preview image blob (equivalent to screenshot_return_mode=both)."
)
@click.option(
    "--preview-only",
    is_flag=True,
    help="Return preview-focused payload (sets screenshot_return_mode=preview)."
)
@click.option(
    "--wait-for-write",
    is_flag=True,
    help="Wait for deterministic screenshot write/import path."
)
@click.option("--width", type=int, default=None, help="Target screenshot width in pixels.")
@click.option("--height", type=int, default=None, help="Target screenshot height in pixels.")
@click.option("--preview-max-width", type=int, default=None, help="Preview max width.")
@click.option("--preview-max-height", type=int, default=None, help="Preview max height.")
@click.option(
    "--preview-format",
    type=click.Choice(["jpg", "png"], case_sensitive=False),
    default=None,
    help="Preview format."
)
@click.option("--preview-jpeg-quality", type=int, default=None, help="Preview JPEG quality (1-100).")
@click.option("--preview-max-pixels", type=int, default=None, help="Preview pixel cap.")
@click.option("--timeout-ms", type=int, default=None, help="Screenshot timeout in milliseconds.")
@handle_unity_errors
def screenshot(
    filename: Optional[str],
    supersize: int,
    with_preview: bool,
    preview_only: bool,
    wait_for_write: bool,
    width: Optional[int],
    height: Optional[int],
    preview_max_width: Optional[int],
    preview_max_height: Optional[int],
    preview_format: Optional[str],
    preview_jpeg_quality: Optional[int],
    preview_max_pixels: Optional[int],
    timeout_ms: Optional[int],
):
    """Capture a screenshot of the scene.

    \b
    Examples:
        unity-mcp scene screenshot
        unity-mcp scene screenshot --filename "level_preview"
        unity-mcp scene screenshot --supersize 2
        unity-mcp scene screenshot --width 640 --height 360
        unity-mcp scene screenshot --with-preview --wait-for-write
    """
    config = get_config()

    preview_requested = with_preview or preview_only or any(
        v is not None for v in (
            preview_max_width,
            preview_max_height,
            preview_format,
            preview_jpeg_quality,
            preview_max_pixels,
        )
    )
    params: dict[str, Any] = {
        "action": "screenshot_with_preview" if preview_requested else "screenshot"
    }
    if filename:
        params["fileName"] = filename
    if supersize > 1:
        params["superSize"] = supersize
    if preview_only:
        params["returnMode"] = "preview"
    elif with_preview or preview_requested:
        params["returnMode"] = "both"
    if wait_for_write:
        params["waitForWrite"] = True
    if width is not None:
        params["width"] = width
    if height is not None:
        params["height"] = height
    if preview_max_width is not None:
        params["previewMaxWidth"] = preview_max_width
    if preview_max_height is not None:
        params["previewMaxHeight"] = preview_max_height
    if preview_format:
        params["previewFormat"] = preview_format
    if preview_jpeg_quality is not None:
        params["previewJpegQuality"] = preview_jpeg_quality
    if preview_max_pixels is not None:
        params["previewMaxPixels"] = preview_max_pixels
    if timeout_ms is not None:
        params["timeoutMs"] = timeout_ms

    result = run_command("manage_scene", params, config)
    click.echo(format_output(result, config.format))
    if result.get("success"):
        print_success("Screenshot captured")
