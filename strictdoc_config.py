"""StrictDoc-required declarative settings; build and test automation is C#.

File-level links are intentional: method names and stable test IDs are recorded
in requirement comments, without claiming automatic C# function-level tracing.
"""
from strictdoc.core.project_config import ProjectConfig
from pathlib import Path


def create_config() -> ProjectConfig:
    """Resolve the checkout independently of the document subdirectory and omit generated/vendor trees."""
    return ProjectConfig(
        project_title="VeloSim bounded RTL requirements",
        source_root_path=str(Path(__file__).resolve().parent),
        project_features=[
            "TABLE_SCREEN", "TRACEABILITY_SCREEN", "DEEP_TRACEABILITY_SCREEN",
            "REQUIREMENT_TO_SOURCE_TRACEABILITY",
        ],
        include_source_paths=[
            "/src/VeloSim.Core/**", "/native/rtl/**", "/native/uhdm/**",
            "/tests/VeloSim.Tests/**", "/tools/**", "/.github/workflows/**",
        ],
        exclude_source_paths=["/artifacts/**", "/external/**", "/.git/**", "/**/bin/**", "/**/obj/**"],
    )
