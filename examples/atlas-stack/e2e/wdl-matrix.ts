// The WDL test matrix. Add an entry per archive to grow coverage — set the file path via env
// (recommended, since WDLs are large/local) or an absolute path, and the expected outcome.
export interface WdlCase {
  /** Human-readable test name. */
  name: string;
  /** Absolute path to the .zip WDL (env override recommended). */
  file: string;
  /** Slug to use; also used to clean up prior jobs for an idempotent run. */
  slug: string;
  /** Optional: override the auto-selected Attach location by exact name (e.g. the base's overworld location). */
  attachLocation?: string;
  expect: {
    /** Dimensions that should be queued (sorted-compared). */
    dimensions: string[];
    /** If set, every job must attach to this location id. */
    attachedTo?: number;
    /** If true, every resulting job must be 'queued' (not needs-match/failed). */
    allQueued?: boolean;
  };
}

const DISCORD_DIR =
  process.env.WDL_DIR ||
  'C:/Source/2b2tAtlas-Public-API/examples/atlas-stack/.artifacts/research/discord-border-wdls';

export const cases: WdlCase[] = [
  {
    name: '+Z Border — overworld + nether, confirmed to +Z Border (904)',
    file: process.env.WDL_PLUS_Z || `${DISCORD_DIR}/1lz_EWe45UzOzRlktc0TZyrjOUEMj3Yaz.zip`,
    slug: 'z-border-2019-04-20',
    attachLocation: '+Z Border',
    expect: { dimensions: ['nether', 'overworld'], attachedTo: 904, allQueued: true },
  },
  // Add more WDL types here, e.g. an overworld-only base, an End-only capture, a ZIP64 archive, etc.
  // {
  //   name: 'Overworld-only base',
  //   file: process.env.WDL_OVERWORLD || `${DISCORD_DIR}/....zip`,
  //   slug: 'some-base-2020-01-01',
  //   expect: { dimensions: ['overworld'], allQueued: true },
  // },
];
