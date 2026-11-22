"""Command line of the common part, from the repository root: python -m common.render <command>."""
import argparse
import sys
from .compiler import build
from .paths import RUNTIME_ASSETS
from .runtime_assets import build_runtime_assets
from .updater import update


def runtime_bundles_ready():
    return (RUNTIME_ASSETS/'bundles/source-bloom.bundle').is_file() and (RUNTIME_ASSETS/'plans').is_dir()


def main():
    parser = argparse.ArgumentParser(prog="python -m common.render", description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('update', help='Update data/game from the emulator, then the runtime assets and the cards')
    commands.add_parser('runtime-assets', help='Rebuild data/runtime-assets from data/game')
    builder = commands.add_parser('build', help='Convert and compile the cards')
    builder.add_argument('cards', nargs='*', help='Card ids; every card by default')
    args = parser.parse_args()
    log = lambda line: print(line, flush=True)  # noqa: E731
    failures = {}
    if args.command == 'update':
        if update(log) or not runtime_bundles_ready():
            build_runtime_assets(log=log)
            _, failures = build(log=log)
    elif args.command == 'runtime-assets':
        build_runtime_assets(log=log)
    else:
        _, failures = build(args.cards or None)
    for card, error in sorted(failures.items())[:50]:
        print(card, error, file=sys.stderr)


if __name__ == '__main__':
    main()
