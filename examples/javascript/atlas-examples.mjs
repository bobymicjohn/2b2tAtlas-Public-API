import http from "node:http";
import https from "node:https";

const API = (process.env.ATLAS_API_BASE_URL || "https://api.blackportal.cloud").replace(/\/$/, "");
const search = process.argv.slice(2).join(" ") || "Mu Megabase";

function get(path) {
  return new Promise((resolve, reject) => {
    const transport = API.startsWith("https:") ? https : http;
    const request = transport.get(`${API}${path}`, {
      headers: {
        Accept: "application/json",
        "User-Agent": "2b2tAtlas-Public-API/1.0",
      },
      timeout: 30_000,
    }, response => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", chunk => { body += chunk; });
      response.on("end", () => {
        if (!response.statusCode || response.statusCode < 200 || response.statusCode >= 300) {
          reject(new Error(`${response.statusCode} ${response.statusMessage}: ${path}`));
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch (error) {
          reject(new Error(`Invalid JSON from ${path}: ${error.message}`));
        }
      });
    });
    request.on("timeout", () => request.destroy(new Error(`Timed out: ${path}`)));
    request.on("error", reject);
  });
}

const normalize = value => value.normalize("NFKC").trim().toLocaleLowerCase("en-US");

async function main() {
  const locations = await get("/api/locations");
  const needle = normalize(search);
  const location = locations.find(item => normalize(item.name) === needle)
    || locations.find(item => normalize(item.name).includes(needle));

  if (!location) throw new Error(`No location matched: ${search}`);

  console.log(`${location.name} [${location.dimensionName}] ${location.x}, ${location.y}, ${location.z}`);
  console.log(location.interactiveUrl);

  const [warps, renders] = await Promise.all([
    get(`/api/warps?locationId=${location.rowid}&limit=1000`),
    get(`/api/renders?locationId=${location.rowid}&limit=1000`),
  ]);
  console.log("warps:", warps.map(item => `/warp ${item.name}`));
  console.log("renders:", renders.map(item => ({
    date: item.worldDownloadDate,
    apiUrl: item.apiUrl,
    sourceWdl: item.worldDownloadUrl || (item.archiveWarp && item.archiveWarp.worldDownloadUrl) || null,
    blueMapUrl: item.blueMapUrl || null,
    blueMapProfileVersion: item.blueMapProfileVersion || null,
  })));

  if (location.groups && location.groups.length) {
    const group = await get(`/api/groups/${location.groups[0].groupId}`);
    console.log(`${group.name}: ${group.locationCount} builds, ${group.highwayCount} highways`);
    console.table(group.locations.slice(0, 5).map(item => ({
      build: item.name,
      role: item.role,
      url: item.locationInteractiveUrl,
    })));
  }

  const highways = await get("/api/highways");
  console.table(highways.filter(item => item.name.includes("+Z")).slice(0, 8).map(item => ({
    name: item.name,
    dimension: item.dimension,
    points: item.points.length,
    apiUrl: item.apiUrl,
  })));
}

main().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
