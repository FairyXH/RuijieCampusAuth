"""Protocol package (mirrors RuijieCampusAuth.Core.Protocols)."""

from .auth_method import AuthMethodKind, display, parse, wants_dhcp_after, wants_dhcp_before  # noqa: F401
from .rc4 import transform as rc4_transform  # noqa: F401
from .private_properties import PrivatePropertiesResult, PrivateProperty, parse as parse_private_properties  # noqa: F401
from .mschap_v2 import MschapChallenge, challenge_hash, generate_nt_response, md4_hash  # noqa: F401
from .peap_vendor_segments import (  # noqa: F401
    CREDENTIAL_IV,
    client_version_segment,
    credential,
    dhcp_phase,
    mac as vendor_mac,
    service_name as vendor_service_name,
)
from .peap_session import IPeapTransport, PeapOptions, PeapResult, TunnelMode, run as run_peap  # noqa: F401
from .clients import (  # noqa: F401
    ClientCenterOptions,
    PortalRequestOptions,
    build_center_url,
    build_url as portal_build_url,
    des_transform,
    get_online_user_info,
    login as portal_login,
    logout as portal_logout,
    page_info as portal_page_info,
    query as client_center_query,
    sam_probe,
    self_service_get,
    upgrade_check,
)
