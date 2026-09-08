#!/usr/bin/env python3
"""Compare released historical observations. No dependencies, logins, or live tracking."""
import argparse
import json
import os
import urllib.parse
import urllib.request


def get(base, path, **query):
    url = base.rstrip('/') + path
    if query:
        url += '?' + urllib.parse.urlencode(query)
    request = urllib.request.Request(url, headers={
        'Accept': 'application/json', 'User-Agent': '2b2tAtlas-Public-API-Nocom/1.1'})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def activity(base, dimension, direction):
    dataset = get(base, '/api/nocom')
    periods = get(base, '/api/nocom/periods', dimension=dimension)
    highways = [] if dimension == 'end' else get(
        base, '/api/nocom/highways', dimension=dimension, direction=direction)
    return dict(dataset=dataset['name'], dimension=dimension,
                periodCount=len(periods), observations=sum(p['observations'] for p in periods),
                direction=None if dimension == 'end' else direction,
                highwayPeriods=[dict(start=r['periodStartUtc'], endExclusive=r['periodEndExclusiveUtc'],
                                     observations=r['observations']) for r in highways],
                interpretation='Historical positive observations, not unique players or current activity.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dimension', choices=['overworld', 'nether', 'end'], default='nether')
    parser.add_argument('--direction', choices=['north','northeast','east','southeast','south','southwest','west','northwest'], default='northeast')
    args = parser.parse_args()
    print(json.dumps(activity(os.environ.get('ATLAS_API_BASE_URL', 'https://api.blackportal.cloud'),
                              args.dimension, args.direction), indent=2))
