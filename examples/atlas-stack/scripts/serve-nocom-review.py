"""Local-only old/new World Pulse review using the real Atlas map renderer."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import mimetypes
from pathlib import Path
from urllib.parse import unquote, urlsplit
from nocom_contrast_tiles import Candidate, TILE

ROOT=Path(__file__).resolve().parent.parent


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',type=Path,default=Path('F:/2b2t/AtlasTiles/Nocom/v1'))
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--port',type=int,default=8772)
    args=parser.parse_args();candidate=Candidate(args.source,args.output)
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            request=unquote(urlsplit(self.path).path)
            try:
                if request in ('/','/index.html'): path=ROOT/'tools/NocomReview/index.html'
                elif request=='/review.js': path=ROOT/'tools/NocomReview/review.js'
                elif request=='/atlas-map.js': path=ROOT/'2b2tAtlas.Client/wwwroot/js/atlas-map.js'
                elif request=='/manifest.json': path=candidate.output/'manifest.json'
                elif request.startswith('/original/') and TILE.fullmatch(request[10:]): path=candidate.source/request[10:]
                elif request.startswith('/candidate/') and TILE.fullmatch(request[11:]): path=candidate.tile(request[11:])
                else: path=None
                if path is None or not path.is_file():
                    self.send_error(404);return
                data=path.read_bytes();self.send_response(200)
                self.send_header('Content-Type',mimetypes.guess_type(path.name)[0] or 'application/octet-stream')
                self.send_header('Content-Length',str(len(data)))
                self.send_header('Cache-Control','no-cache' if path.suffix in ('.js','.html','.json') else 'public,max-age=3600')
                self.end_headers();self.wfile.write(data)
            except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError): pass
            except Exception as exc:
                print(f'Review error: {request}: {exc}',flush=True);self.send_error(500)
        def log_message(self,format,*args): pass
    with ThreadingHTTPServer(('127.0.0.1',args.port),Handler) as server:
        print(f'Nocom review: http://127.0.0.1:{args.port}/ ; candidate {candidate.output}',flush=True)
        server.serve_forever()


if __name__=='__main__': main()
