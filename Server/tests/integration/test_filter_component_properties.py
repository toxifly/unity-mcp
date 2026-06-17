"""
Tests for the filter_component_properties utility.
"""
from services.tools.utils import filter_component_properties


def test_no_filters_returns_unchanged():
    """When neither whitelist nor blacklist is set, components are returned unchanged."""
    components = [
        {"typeName": "Rigidbody", "instanceID": 1, "properties": {"mass": 1.0, "drag": 0.5}},
    ]
    result, was_filtered = filter_component_properties(components, None, None)
    assert result is components
    assert was_filtered is False


def test_whitelist_keeps_only_specified():
    """Whitelist keeps only the specified property keys."""
    components = [
        {"typeName": "Rigidbody", "instanceID": 1, "properties": {"mass": 1.0, "drag": 0.5, "angularDrag": 0.1}},
    ]
    result, was_filtered = filter_component_properties(components, whitelist=["mass"], blacklist=None)
    assert was_filtered is True
    assert result[0]["properties"] == {"mass": 1.0}
    assert result[0]["typeName"] == "Rigidbody"
    assert result[0]["instanceID"] == 1


def test_blacklist_removes_specified():
    """Blacklist removes the specified property keys."""
    components = [
        {"typeName": "Rigidbody", "instanceID": 1, "properties": {"mass": 1.0, "drag": 0.5, "angularDrag": 0.1}},
    ]
    result, was_filtered = filter_component_properties(components, whitelist=None, blacklist=["drag", "angularDrag"])
    assert was_filtered is True
    assert result[0]["properties"] == {"mass": 1.0}


def test_whitelist_and_blacklist_combined():
    """When both are set, whitelist is applied first, then blacklist."""
    components = [
        {"typeName": "Rigidbody", "instanceID": 1, "properties": {"mass": 1.0, "drag": 0.5, "angularDrag": 0.1}},
    ]
    result, was_filtered = filter_component_properties(
        components, whitelist=["mass", "drag"], blacklist=["drag"],
    )
    assert was_filtered is True
    assert result[0]["properties"] == {"mass": 1.0}


def test_metadata_preserved():
    """typeName and instanceID are always preserved regardless of filters."""
    components = [
        {"typeName": "BoxCollider", "instanceID": 42, "properties": {"size": [1, 1, 1]}},
    ]
    result, _ = filter_component_properties(components, whitelist=["nonexistent"], blacklist=None)
    assert result[0]["typeName"] == "BoxCollider"
    assert result[0]["instanceID"] == 42
    assert result[0]["properties"] == {}


def test_component_without_properties_dict():
    """Components without a properties sub-dict are passed through."""
    components = [
        {"typeName": "Transform", "instanceID": 5},
    ]
    result, was_filtered = filter_component_properties(components, whitelist=["mass"], blacklist=None)
    assert was_filtered is True
    assert result[0] == {"typeName": "Transform", "instanceID": 5}


def test_non_dict_component_passthrough():
    """Non-dict items in the list are passed through unchanged."""
    components = ["not a dict", 123]
    result, was_filtered = filter_component_properties(components, whitelist=["x"], blacklist=None)
    assert was_filtered is True
    assert result == ["not a dict", 123]


def test_multiple_components():
    """Filter applies to all components in the list."""
    components = [
        {"typeName": "A", "instanceID": 1, "properties": {"x": 1, "y": 2}},
        {"typeName": "B", "instanceID": 2, "properties": {"x": 10, "z": 30}},
    ]
    result, was_filtered = filter_component_properties(components, whitelist=["x"], blacklist=None)
    assert was_filtered is True
    assert result[0]["properties"] == {"x": 1}
    assert result[1]["properties"] == {"x": 10}


def test_extra_top_level_keys_preserved():
    """Extra top-level keys (like 'enabled') are preserved."""
    components = [
        {"typeName": "Light", "instanceID": 7, "enabled": True, "properties": {"intensity": 1.0, "color": "white"}},
    ]
    result, _ = filter_component_properties(components, blacklist=["color"], whitelist=None)
    assert result[0]["enabled"] is True
    assert result[0]["properties"] == {"intensity": 1.0}
