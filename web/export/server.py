"""A local server laid out as a host would serve the site: web/engine/ at the root, data/web-cards/ under /web-cards/.

It answers byte ranges as hosts do: a browser needs them to seek in a movie.
"""
import http.server
import os
import re
import threading
import urllib.parse
from .paths import ENGINE, WEB_CARDS


class SiteHandler(http.server.SimpleHTTPRequestHandler):
    def translate_path(self, path):
        route = urllib.parse.unquote(urllib.parse.urlsplit(path).path)
        if route == '/web-cards' or route.startswith('/web-cards/'):
            self.directory = str(WEB_CARDS)
            path = path[len('/web-cards'):] or '/'
        else:
            self.directory = str(ENGINE)
        return super().translate_path(path)

    def send_head(self):
        match = re.fullmatch(r'bytes=(\d*)-(\d*)', self.headers.get('Range', ''))
        path = self.translate_path(self.path)
        if not match or not os.path.isfile(path):
            return super().send_head()
        size = os.path.getsize(path)
        first, last = match.groups()
        start = int(first) if first else max(0, size-int(last or 0))
        end = min(int(last), size-1) if first and last else size-1
        if start >= size or start > end:
            self.send_error(416, 'Requested range not satisfiable')
            return None
        stream = open(path, 'rb')
        stream.seek(start)
        self.send_response(206)
        self.send_header('Content-Type', self.guess_type(path))
        self.send_header('Content-Range', f'bytes {start}-{end}/{size}')
        self.send_header('Content-Length', str(end-start+1))
        self.send_header('Accept-Ranges', 'bytes')
        self.end_headers()
        self.remaining = end-start+1
        return stream

    def copyfile(self, source, outputfile):
        remaining = getattr(self, 'remaining', None)
        if remaining is None:
            return super().copyfile(source, outputfile)
        try:
            while remaining > 0:
                chunk = source.read(min(65536, remaining))
                if not chunk:
                    break
                outputfile.write(chunk)
                remaining -= len(chunk)
        except ConnectionError:
            pass  # a browser drops a range it no longer needs as soon as it seeks again

    def end_headers(self):
        if not hasattr(self, 'remaining'):
            self.send_header('Accept-Ranges', 'bytes')
        super().end_headers()

    def log_message(self, *args):
        pass


def serve(handler=SiteHandler, port=0):
    """Start the server in the background; returns it and its address, ending with a slash."""
    server = http.server.ThreadingHTTPServer(('127.0.0.1', port), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f'http://127.0.0.1:{server.server_address[1]}/'
