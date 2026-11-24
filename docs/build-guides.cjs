// Regenerate committed offline guides after editing USER-GUIDE*.md. Developer tool only.
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');
const escape = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
function url(value) {
  if (value === 'USER-GUIDE.md') return 'user-guide.ru.html';
  if (value === 'USER-GUIDE.en.md') return 'user-guide.en.html';
  if (/^README(?:\.en)?\.md$/.test(value)) return 'https://github.com/ub3dqy/jtdx-autocq/blob/main/' + value;
  if (value.startsWith('docs/')) return value.slice(5);
  return value;
}
function inline(value) {
  const code = [];
  value = escape(value).replace(/`([^`]+)`/g, (_, s) => '\x00' + (code.push('<code>' + s + '</code>') - 1) + '\x00');
  value = value.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, (_, alt, src) => '<img loading="lazy" src="' + url(src) + '" alt="' + alt + '">');
  value = value.replace(/\[([^\]]+)\]\(([^)]+)\)/g, (_, text, target) => '<a href="' + url(target) + '">' + text + '</a>');
  value = value.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
  return value.replace(/\x00(\d+)\x00/g, (_, i) => code[i]);
}
function render(source) {
  const lines = source.split(/\r?\n/), out = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.trim()) continue;
    if (line.startsWith('```')) {
      const block = [];
      while (++i < lines.length && !lines[i].startsWith('```')) block.push(lines[i]);
      out.push('<pre><code>' + escape(block.join('\n')) + '</code></pre>'); continue;
    }
    const heading = /^(#{1,6}) (.+)$/.exec(line);
    if (heading) {
      const n = heading[1].length;
      const id = ['Поиск для дипломов HAMLOG', 'Search for HAMLOG award contacts'].includes(heading[2]) ? ' id="hamlog-awards"' : '';
      out.push('<h' + n + id + '>' + inline(heading[2]) + '</h' + n + '>');
      continue;
    }
    if (line.startsWith('|') && /^\|[\s:|-]+\|$/.test(lines[i + 1] || '')) {
      const cells = s => s.trim().slice(1, -1).split('|').map(v => v.trim());
      out.push('<div class="table-scroll"><table><thead><tr>' + cells(line).map(c => '<th>' + inline(c) + '</th>').join('') + '</tr></thead><tbody>'); i++;
      while ((lines[i + 1] || '').startsWith('|')) out.push('<tr>' + cells(lines[++i]).map(c => '<td>' + inline(c) + '</td>').join('') + '</tr>');
      out.push('</tbody></table></div>'); continue;
    }
    if (/^\d+\. |^- /.test(line)) {
      const ordered = /^\d/.test(line), pattern = ordered ? /^\d+\. / : /^- /, tag = ordered ? 'ol' : 'ul';
      out.push('<' + tag + '>');
      do { out.push('<li>' + inline(lines[i].replace(pattern, '')) + '</li>'); i++; } while (i < lines.length && pattern.test(lines[i]));
      i--; out.push('</' + tag + '>'); continue;
    }
    const paragraph = [line];
    while (i + 1 < lines.length && lines[i + 1].trim() && !/^(#|\||\d+\. |\- |```)/.test(lines[i + 1])) paragraph.push(lines[++i]);
    out.push('<p>' + inline(paragraph.join(' ')) + '</p>');
  }
  return out.join('\n');
}
for (const [language, name, title] of [['ru', 'USER-GUIDE.md', 'Как пользоваться AutoCQ'], ['en', 'USER-GUIDE.en.md', 'Using AutoCQ']]) {
  const html = '<!doctype html>\n<html lang="' + language + '"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="author" content="UB3DQY"><title>' + title + ' · UB3DQY</title><link rel="icon" href="icon.png"><link rel="stylesheet" href="guide.css"></head><body><main>' + render(fs.readFileSync(path.join(root, name), 'utf8')) + '</main></body></html>\n';
  fs.writeFileSync(path.join(__dirname, 'user-guide.' + language + '.html'), html);
}
console.log('Generated Russian and English offline guides.');
