"""Command line of the web export, from the repository root: python -m web.export <command>."""
import argparse
import sys
import time
from .build import build_web, write_catalog
from .paths import LANGUAGES


def main():
    parser = argparse.ArgumentParser(prog='python -m web.export', description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    web = commands.add_parser('build', help='Build the browser packages in data/web-cards')
    web.add_argument('cards', nargs='*', help='Card ids; every card by default')
    web.add_argument('--languages', nargs='+', default=LANGUAGES, choices=LANGUAGES)
    web.add_argument('--resume', action='store_true', help='Keep the packages already built')
    web.add_argument('--workers', type=int, help='Processes; by default as many as free memory allows, at most 8')
    commands.add_parser('catalog', help='Rebuild data/web-cards/catalog.json for the gallery')
    posters = commands.add_parser('posters', help='Draw the poster of each card per language, shown while it loads')
    posters.add_argument('cards', nargs='*', help='Card ids; every card of the catalog by default')
    posters.add_argument('--part', default='1/1', help='Slice i/n of the catalog, to share the work between processes')
    posters.add_argument('--force', action='store_true', help='Redraw even the posters that are up to date')
    posters.add_argument('--languages', nargs='+', default=LANGUAGES, choices=LANGUAGES, help='Only these languages; all by default')
    posters.add_argument('--gold', action='store_true', help="Also the gold frame's posters, on the cards that have one (about 3 GB more)")
    server = commands.add_parser('serve', help='Serve web/engine and data/web-cards locally, as a host would')
    server.add_argument('--port', type=int, default=8765)
    args = parser.parse_args()
    if args.command == 'serve':
        from .server import serve
        running, address = serve(port=args.port)
        print(f'{address}index.html?card=PK_10_000010_00  ·  {address}gallery.html  ·  Ctrl+C to stop', flush=True)
        try:
            while True:
                time.sleep(3600)
        except KeyboardInterrupt:
            running.shutdown()
    elif args.command == 'posters':
        from .posters import build_posters, catalog_cards
        index, count = map(int, args.part.split('/'))
        cards = (args.cards or catalog_cards())[index-1::count]
        failures = build_posters(cards, args.languages, force=args.force, gold=args.gold)
        for name, error in sorted(failures.items()):
            print(name, error, file=sys.stderr)
        sys.exit(1 if failures else 0)
    elif args.command == 'catalog':
        print(write_catalog(), 'cards')
    else:
        failures = build_web(args.cards or None, args.languages, workers=args.workers, force=not args.resume)
        for card, error in sorted(failures.items()):
            print(card, error, file=sys.stderr)
        sys.exit(1 if failures else 0)


if __name__ == '__main__':
    main()
