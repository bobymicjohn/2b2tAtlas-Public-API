// Unit validation for the generated comparison UI; no browser or network required.
const fs = require('fs'), vm = require('vm'), assert = require('assert');
const root = process.argv[2];
const html = fs.readFileSync(root + '/nocom/index.html', 'utf8');
const data = JSON.parse(html.match(/<script id="highway-data" type="application\/json">([\s\S]*?)<\/script>/)[1]);
const summary = JSON.parse(fs.readFileSync(root + '/nocom/highway-summary.json', 'utf8'));
assert.equal(data.length, 272); assert.equal(summary.length, 16);
const rows = summary.map(item => {
  const elements = { '.highway-count': {}, meter: {}, '.highway-share': {} };
  return { dataset: { highwayDimension: item.dimension, highwayDirection: item.direction },
    querySelector: key => elements[key], elements };
});
const select = { value: '', addEventListener: (_, handler) => { select.change = handler; } };
const status = {};
const document = {
  getElementById: id => id === 'highway-data' ? { textContent: JSON.stringify(data) } : id === 'highway-period' ? select : status,
  querySelectorAll: () => rows,
};
const script = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]).find(s => s.includes('highway-period'));
assert(script); vm.runInNewContext(script, { document });
const periods = ['', ...new Set(data.map(r => r.periodStartUtc))];
for (const period of periods) {
  select.value = period; select.change();
  for (const row of rows) {
    const source = data.filter(r => r.dimension === row.dataset.highwayDimension && (!period || r.periodStartUtc === period));
    const count = source.filter(r => r.direction === row.dataset.highwayDirection).reduce((sum, r) => sum + r.observations, 0);
    const total = source.reduce((sum, r) => sum + r.observations, 0);
    assert.equal(row.elements['.highway-count'].textContent, count.toLocaleString('en-US'));
    assert.equal(row.elements.meter.value, total ? count / total : 0);
    if (!period) assert.equal(summary.find(s => s.dimension === row.dataset.highwayDimension && s.direction === row.dataset.highwayDirection).observations, count);
  }
}
assert(html.includes('not a ranking of unique travelers'));
console.log(JSON.stringify({ periodsTested: periods.length, directionRows: rows.length, comparisonsChecked: periods.length * rows.length, sourceRecords: data.length }));
