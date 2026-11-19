import sys

"""Entry point: no args -> interactive menu; args -> argparse subcommands."""

from .cli import main
from .menu import run_menu


if __name__ == "__main__":
    if len(sys.argv) <= 1:
        raise SystemExit(run_menu())
    raise SystemExit(main())
