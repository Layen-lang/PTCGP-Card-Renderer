// Browses web-cards/catalog.json, by scrolling. Cards near the screen are live; the others give back what they hold.
import './src/pokemon-card.js';

const LANGUAGES = { fr_FR: 'Français', en_US: 'English', ja_JP: '日本語', it_IT: 'Italiano', de_DE: 'Deutsch', es_ES: 'Español', pt_BR: 'Português', ko_KR: '한국어', zh_TW: '繁體中文' };
const COMPONENT_LANGUAGE = { fr_FR: 'fr', en_US: 'en', ja_JP: 'ja', it_IT: 'it', de_DE: 'de', es_ES: 'es', pt_BR: 'pt', ko_KR: 'ko', zh_TW: 'zh' };
// PokemonTypes in the master data.
const TYPES = { 1: 'Colorless', 2: 'Grass', 3: 'Fire', 4: 'Water', 5: 'Lightning', 6: 'Psychic', 7: 'Fighting', 8: 'Darkness', 9: 'Metal', 10: 'Dragon' };
const RARITIES = ['C', 'U', 'R', 'RR', 'AR', 'SR', 'SAR', 'IM', 'S', 'SSR', 'AN', 'UR'];
const KINDS = { Pokemon: 'Pokémon', Trainers: 'Trainer' };
// All cards share one WebGL context: what limits them is the memory of their textures.
const MAX_LIVE = 48;
const BATCH = 24;
// A card passed at speed is not worth loading.
const SETTLE_MS = 120;
// Ahead of the scroll: cards one screen away are ready but not drawn, three screens away their
// files are already in the browser's cache. Passing a card should never wait for it.
const READY_AHEAD = '100% 0px';
const FETCH_AHEAD = '300% 0px';
const FETCHES_AT_ONCE = 4;

const element = id => document.getElementById(id);
const query = new URLSearchParams(location.search);
const base = query.get('base') ?? new URL('../web-cards/', location.href).pathname;
const state = {
    search: query.get('q') ?? '', extension: query.get('extension') ?? '', family: query.get('family') ?? '', language: query.get('lang') ?? 'en_US',
    columns: +(query.get('columns') ?? 6),
    // The gold frame, on the cards that have one.
    gold: query.has('gold'),
    rarity: new Set(query.getAll('rarity')), type: new Set(query.getAll('type')), kind: new Set(query.getAll('kind')),
};
let catalog, cards = [], added = 0, settling;
const slots = new Map();

const nameOf = card => card.names[state.language] ?? card.names.en_US ?? card.id;
const normalized = text => text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase();

// All the filters except one: each list counts what choosing in it would give.
function matching(without) {
    const search = normalized(state.search.trim());
    return catalog.cards.filter(card => (
        (without === 'search' || !search || normalized(nameOf(card)).includes(search) || card.id.toLowerCase().includes(search))
        && (without === 'extension' || !state.extension || card.places.some(place => place.extension === state.extension))
        && (without === 'family' || !state.family || card.family === state.family)
        && (without === 'rarity' || !state.rarity.size || state.rarity.has(card.rarity))
        && (without === 'type' || !state.type.size || card.types.some(type => state.type.has(String(type))))
        && (without === 'kind' || !state.kind.size || state.kind.has(card.kind))
    ));
}

function tally(cards, values) {
    const counts = new Map();
    for (const card of cards) for (const value of values(card)) counts.set(value, (counts.get(value) ?? 0) + 1);
    return counts;
}

function fillSelect(select, everything, rows, value) {
    select.replaceChildren(new Option(everything, ''), ...rows.map(([id, label]) => new Option(label, id)));
    select.value = value;
}

function fillChips(holder, chosen, rows) {
    holder.replaceChildren(...rows.map(([id, label, count]) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('aria-pressed', chosen.has(id));
        button.disabled = !count && !chosen.has(id);
        button.append(`${label} `);
        button.append(Object.assign(document.createElement('small'), { textContent: count }));
        button.onclick = () => {
            if (!chosen.delete(id)) chosen.add(id);
            changed();
        };
        return button;
    }));
}

function refreshFilters() {
    const extensions = tally(matching('extension'), card => new Set(card.places.map(place => place.extension)));
    fillSelect(element('extension'), 'All', catalog.extensions.filter(row => extensions.has(row.id) || row.id === state.extension).map(row => [row.id, `${row.id} · ${row.name} (${extensions.get(row.id) ?? 0})`]), state.extension);
    const families = tally(matching('family'), card => card.family ? [card.family] : []);
    fillSelect(element('family'), 'All', [...families].sort(([a], [b]) => a.localeCompare(b, 'en')).map(([family, count]) => [family, `${family} (${count})`]), state.family);
    const rarities = tally(matching('rarity'), card => [card.rarity]);
    fillChips(element('rarity'), state.rarity, catalog.rarities.map(rarity => [rarity, rarity, rarities.get(rarity) ?? 0]));
    const types = tally(matching('type'), card => card.types.map(String));
    fillChips(element('type'), state.type, Object.entries(TYPES).map(([id, label]) => [id, label, types.get(id) ?? 0]));
    const kinds = tally(matching('kind'), card => [card.kind]);
    fillChips(element('kind'), state.kind, Object.entries(KINDS).map(([id, label]) => [id, label, kinds.get(id) ?? 0]));
}

function writeAddress() {
    const out = new URLSearchParams();
    for (const [name, value] of [['q', state.search], ['extension', state.extension], ['family', state.family]]) if (value) out.set(name, value);
    if (state.language !== 'en_US') out.set('lang', state.language);
    if (state.columns !== 6) out.set('columns', state.columns);
    if (state.gold) out.set('gold', '');
    for (const name of ['rarity', 'type', 'kind']) for (const value of state[name]) out.append(name, value);
    if (query.has('base')) out.set('base', query.get('base'));
    history.replaceState(null, '', `${location.pathname}${out.size ? `?${out}` : ''}`);
}

function makeCard(card, attributes) {
    const live = document.createElement('pokemon-card');
    live.setAttribute('base', base);
    live.setAttribute('lang', COMPONENT_LANGUAGE[state.language]);
    for (const [name, value] of Object.entries(attributes)) live.setAttribute(name, value);
    if (state.gold && card.gold) live.setAttribute('gold', '');
    live.setAttribute('card', card.id);
    return live;
}

function releaseCard(slot) {
    slot.live?.release();
    slot.live?.remove();
    slot.live = null;
    if (slot.poster) slot.poster.hidden = false;
}

// Loaded just below the screen, drawn only once on it, and not behind the large view.
function conceal(slot) {
    if (slot.live) slot.live.offscreen = !slot.visible || element('viewer').open;
}

function mountCard(slot) {
    slot.live = makeCard(slot.card, { preview: '' });
    // The slot's poster stays under the card until it draws: tilted, the card would uncover it.
    slot.live.addEventListener('cardready', () => {
        conceal(slot);
        if (slot.poster) slot.poster.hidden = true;
    }, { once: true });
    slot.live.addEventListener('cardfailed', () => {
        releaseCard(slot);
        slot.failed = true;
        slot.holder.textContent = 'Unavailable';
        slot.holder.classList.add('failed');
    });
    slot.holder.append(slot.live);
}

// The cards closest to the middle of the screen come first; those the scroll has left
// behind count twice as far as those it is heading to.
let lastScroll = scrollY, heading = 1;
addEventListener('scroll', () => {
    if (scrollY !== lastScroll) heading = Math.sign(scrollY - lastScroll);
    lastScroll = scrollY;
}, { passive: true });

function settle() {
    const middle = innerHeight / 2, distance = slot => {
        const box = slot.holder.getBoundingClientRect(), offset = (box.top + box.bottom) / 2 - middle;
        return Math.sign(offset) === heading ? Math.abs(offset) : Math.abs(offset) * 2;
    };
    const wanted = new Set([...slots.values()].filter(slot => slot.near && !slot.failed).sort((a, b) => distance(a) - distance(b)).slice(0, MAX_LIVE));
    for (const slot of slots.values()) if (slot.live && !wanted.has(slot)) releaseCard(slot);
    for (const slot of wanted) if (!slot.live) mountCard(slot);
}

function settleSoon() {
    clearTimeout(settling);
    settling = setTimeout(settle, SETTLE_MS);
}

const nearScreen = new IntersectionObserver(entries => {
    for (const entry of entries) slots.get(entry.target).near = entry.isIntersecting;
    settleSoon();
}, { rootMargin: READY_AHEAD });

const onScreen = new IntersectionObserver(entries => {
    for (const entry of entries) {
        const slot = slots.get(entry.target);
        slot.visible = entry.isIntersecting;
        conceal(slot);
    }
});

// The player knows which files a card needs: the gallery only says which cards come next.
const fetchQueue = [];
let fetching = 0;

function pumpFetches() {
    while (fetching < FETCHES_AT_ONCE && fetchQueue.length) {
        const card = fetchQueue.shift();
        fetching++;
        customElements.get('pokemon-card').preload(card.id, { lang: COMPONENT_LANGUAGE[state.language], base, gold: state.gold && !!card.gold })
            .catch(() => {})
            .finally(() => { fetching--; pumpFetches(); });
    }
}

const fetchAhead = new IntersectionObserver(entries => {
    for (const entry of entries) {
        const slot = slots.get(entry.target);
        if (!entry.isIntersecting || slot.prefetched) continue;
        slot.prefetched = true;
        fetchQueue.push(slot.card);
    }
    pumpFetches();
}, { rootMargin: FETCH_AHEAD });

const nearEnd = new IntersectionObserver(entries => {
    if (entries.some(entry => entry.isIntersecting)) addCards();
}, { rootMargin: '300% 0px' });

function releaseGrid() {
    clearTimeout(settling);
    nearScreen.disconnect();
    fetchAhead.disconnect();
    fetchQueue.length = 0;
    onScreen.disconnect();
    for (const slot of slots.values()) releaseCard(slot);
    slots.clear();
    element('grid').replaceChildren();
}

function placeLabel(card) {
    const place = card.places[0];
    return place ? `${place.extension} #${place.number}` : card.id;
}

function setColumns() {
    element('grid').style.setProperty('--columns', innerWidth <= 760 ? 2 : state.columns);
}

function addCards() {
    const next = cards.slice(added, added + BATCH);
    added += next.length;
    for (const card of next) {
        const figure = document.createElement('figure'), holder = Object.assign(document.createElement('div'), { className: 'slot' });
        // The card drawn in advance, while the live one is far from the screen or loading.
        const poster = Object.assign(document.createElement('img'), { className: 'poster', alt: '', decoding: 'async',
            src: `${base}_posters/${card.id}/${COMPONENT_LANGUAGE[state.language]}${state.gold && card.gold ? '-gold' : ''}.webp` });
        poster.onerror = () => poster.remove();
        holder.append(poster);
        const caption = document.createElement('figcaption'), open = document.createElement('button');
        open.type = 'button';
        open.append(Object.assign(document.createElement('strong'), { textContent: nameOf(card) }));
        open.append(Object.assign(document.createElement('span'), { textContent: `${card.rarity} · ${placeLabel(card)}` }));
        open.title = 'View larger';
        open.onclick = () => openViewer(card);
        caption.append(open);
        figure.append(holder, caption);
        element('grid').append(figure);
        slots.set(holder, { card, holder, poster, live: null, near: false, visible: false, failed: false });
        nearScreen.observe(holder);
        onScreen.observe(holder);
        fetchAhead.observe(holder);
    }
    // Still in reach after this batch: the observer only speaks when something changes.
    nearEnd.disconnect();
    if (added < cards.length) nearEnd.observe(element('end'));
}

function changed() {
    releaseGrid();
    refreshFilters();
    cards = matching();
    added = 0;
    setColumns();
    element('count').textContent = `${cards.length} card${cards.length === 1 ? '' : 's'}`;
    element('empty').hidden = cards.length > 0;
    scrollTo(0, 0);
    addCards();
    writeAddress();
}

function openViewer(card) {
    const viewer = element('viewer');
    viewer.querySelector('pokemon-card')?.remove();
    element('viewer-title').textContent = `${nameOf(card)} · ${card.rarity} · ${placeLabel(card)}${card.immersive ? ' · long press for the movie' : ''}`;
    viewer.showModal();
    settle();
    for (const slot of slots.values()) conceal(slot);
    viewer.append(makeCard(card, {}));
}

function closeViewer() {
    const live = element('viewer').querySelector('pokemon-card');
    live?.release();
    live?.remove();
    for (const slot of slots.values()) conceal(slot);
    settleSoon();
}

async function start() {
    const response = await fetch(`${base}catalog.json`);
    if (!response.ok) throw new Error('catalog.json is missing: run python -m web.export catalog');
    catalog = await response.json();
    const known = new Set(catalog.cards.map(card => card.rarity));
    catalog.rarities = [...RARITIES.filter(rarity => known.delete(rarity)), ...known];
    fillSelect(element('language'), '', Object.entries(LANGUAGES), state.language);
    element('language').options[0].remove();
    element('search').value = state.search;
    element('columns').value = state.columns;
    let typing;
    element('search').oninput = event => {
        state.search = event.target.value;
        clearTimeout(typing);
        typing = setTimeout(changed, 250);
    };
    for (const name of ['extension', 'family']) element(name).onchange = event => { state[name] = event.target.value; changed(); };
    element('language').onchange = event => { state.language = event.target.value; changed(); };
    element('columns').onchange = event => { state.columns = +event.target.value; changed(); };
    element('gold').checked = state.gold;
    element('gold').onchange = event => { state.gold = event.target.checked; changed(); };
    element('reset').onclick = () => {
        Object.assign(state, { search: '', extension: '', family: '' });
        element('search').value = '';
        for (const name of ['rarity', 'type', 'kind']) state[name].clear();
        changed();
    };
    element('close').onclick = () => element('viewer').close();
    element('viewer').addEventListener('close', closeViewer);
    addEventListener('resize', () => { setColumns(); settleSoon(); });
    changed();
}

start().catch(error => { element('count').textContent = error.message; });
