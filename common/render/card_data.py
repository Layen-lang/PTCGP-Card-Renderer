"""Join master records with localized source strings for one card."""
import json
import re
import struct
import zlib
import UnityPy
from .card_names import resolve_name
from .paths import PACKAGE_DATA, RARITIES


def read_groups(data, names):
    """Read named KVRF groups without merging singular/plural keys."""
    if data[:4] != b'KVRF':
        raise ValueError('Invalid localization header')

    def u32(offset):
        return struct.unpack_from('<I', data, offset)[0]
    entries = {}
    seen = set()
    for bucket in range(u32(0x14)):
        offset = u32(u32(0x20)+bucket*4)
        while offset:
            if offset in seen:
                raise ValueError(f'Duplicate localization entry: {offset}')
            seen.add(offset)
            packed = struct.unpack_from('<H', data, offset+8)[0]
            if packed & 7 == 1:  # KvromKeyTypes.String
                size = u32(offset+12)
                body = data[offset+16:offset+16+size]
                if len(body) != size:
                    raise ValueError('Truncated localization entry')
                compression = data[offset+10]
                if compression == 1:
                    body = zlib.decompress(body[4:], -15)
                elif compression != 0:
                    raise ValueError(f'Unknown localization compression: {compression}')
                key_size = struct.unpack_from('<H', body)[0]
                if 2+key_size > len(body):
                    raise ValueError('Truncated localization key')
                key = body[2:2+key_size].decode('utf-16-le')
                identity = (packed >> 3, key)
                if identity in entries:
                    raise ValueError(f'Duplicate scoped localization key: {identity}')
                entries[identity] = body[2+key_size:]
            offset = u32(offset)
    groups = {}
    for name in names:
        # Languages without plurals (ja, ko, zh) have no _plural group.
        if (1, name) not in entries:
            continue
        # DataCategoryTag records map original group names to category IDs.
        category = struct.unpack('<H', entries[1, name])[0]
        groups[name] = dict(category=category, labels={
            key: value.decode('utf-16-le')
            for (group, key), value in entries.items() if group == category})
    return groups


class MessageNames:
    """Names referenced from card text (LocLabelTagResolverImpl, 1.7.5)."""
    EX_REGEX = re.compile(r'\s?-?(ex)$')

    def __init__(self, game, language):
        self.game = game
        self.master = game.locale(language)['Master.bytes']

    def character(self, identifier):
        # ResolveCardNameTag (RVA 0x496E444) turns a _EX character's suffix into
        # the ex image through String.Replace, hence every occurrence.
        label = self.master[self.game.table('Character').one('CharacterID', identifier)['DisplayNameMSID']].replace('\n', '')
        match = self.EX_REGEX.search(label) if identifier.endswith('_EX') else None
        return label.replace(match[1], '[Img:ex]') if match else label

    def card(self, card_id):
        # GetCardWithSubNameText (RVA 0x4970504); the only cards using it have no sub-name.
        card = self.game.table('TrainerCard').one('CardID', card_id)
        if card['RightEndDisplayNameMSID']:
            raise ValueError(f'Sub-name of a referenced card is not ported: {card_id}')
        return self.character(self.game.table('Trainer').one('TrainerID', card['TrainerID'])['CharacterID'])

    def attack(self, identifier):
        return self.master[self.game.table('PokemonAttackName').one('PokemonAttackNameID', identifier)['NameMSID']]

    def ability(self, identifier, suffix):
        # GetPokemonAbilityNameText (RVA 0x4970D4C): the suffixed label only if it exists.
        key = self.game.table('PokemonAbilityName').one('PokemonAbilityNameID', identifier)['NameMSID']
        return self.master.get(f'{key}_{suffix}', self.master[key]) if suffix else self.master[key]

    def evolution_pokemon(self, stage):
        # ResolveEvolutionPokemonTag (RVA 0x496F5B4); the plural labels are all empty.
        return self.master[f'EVOLUTION_STAGE_POKEMON_{stage}']


class CardData:
    def __init__(self, game):
        self.game = game
        self._scoped = {}
        self.trainer_proof = json.loads((PACKAGE_DATA/'trainer-labels-source.json').read_text(encoding='utf-8'))

    def scoped(self, language):
        """Scoped groups from the active Common/Locale bundle (delta-safe)."""
        groups = self._scoped.get(language)
        if groups is None:
            row = self.game.by_name[f'Common/Locale/{language}_bundles']
            candidates = []
            for obj in UnityPy.load(str(self.game.path(row))).objects:
                if obj.type.name != 'TextAsset':
                    continue
                text = obj.read()
                if text.m_Name not in ('Master', 'Master.bytes'):
                    continue
                raw = text.m_Script
                candidates.append(raw.encode('utf-8', 'surrogateescape') if isinstance(raw, str) else bytes(raw))
            if len(candidates) != 1 or not candidates[0].startswith(b'KVRF'):
                raise ValueError(f'TextAsset KVRF Master non unique : {language}')
            groups = self._scoped[language] = read_groups(candidates[0], ('LocalizeMasterSpecialConditionType', 'LocalizeMasterSpecialConditionType_plural', 'LocalizeMasterAdditionalName'))
        return groups

    def special_conditions(self, language):
        groups = self.scoped(language)
        singular = groups['LocalizeMasterSpecialConditionType']
        return dict(singular=singular['labels'], plural=groups.get('LocalizeMasterSpecialConditionType_plural', singular)['labels'])

    def load(self, card_id, language):
        return self.trainer(card_id, language) if card_id.startswith('TR_') else self.pokemon(card_id, language)

    def pokemon(self, card_id, language):
        table = self.game.table
        card = table('PokemonCard').one('CardID', card_id)
        if card['IllustrationID'].rsplit('_', 1)[-1] not in RARITIES:
            raise ValueError('Pokemon rarity outside the L loading scope')
        pokemon = table('Pokemon').one('PokemonID', card['PokemonID'])
        pokedex = table('Pokedex').one('PokedexID', card['PokedexID'])
        character = table('Character').one('CharacterID', pokemon['CharacterID'])
        attacks, names = table('PokemonAttack'), table('PokemonAttackName')
        files = self.game.locale(language)
        additional_names = self.scoped(language)['LocalizeMasterAdditionalName']['labels']

        def localize(key, file='Master.bytes'):
            return files[file][key] if key else ''
        attack_records = []
        for attack_id in pokemon['PokemonAttackIDs']:
            attack = attacks.one('PokemonAttackID', attack_id)
            name = names.one('PokemonAttackNameID', attack['PokemonAttackNameID'])
            attack_records.append(dict(record=attack, name_record=name, name=localize(name['NameMSID']),
                                       description_template=localize(attack['DescriptionMSID'])))
        ability_records = []
        if pokemon['PokemonAbilityIDs']:
            abilities, ability_names = table('PokemonAbility'), table('PokemonAbilityName')
            for ability_id in pokemon['PokemonAbilityIDs']:
                ability = abilities.one('PokemonAbilityID', ability_id)
                name = ability_names.one('PokemonAbilityNameID', ability['PokemonAbilityNameID'])
                ability_records.append(dict(record=ability, name=localize(name['NameMSID']),
                                            description_template=localize(ability['DescriptionMSID'])))
        characters = table('Character')

        def character_name(identifier):
            return localize(characters.one('CharacterID', identifier)['DisplayNameMSID']) if identifier else ''
        source_name = character_name(pokemon['PreevolvedCharacterID'])
        mega_source_name = character_name(pokemon.get('MegaEvolutionPreevolvedCharacterID', ''))
        name_template = localize(character['DisplayNameMSID'])
        labels = {k: localize(k, 'UI.bytes') for k in ('hp', 'weakness', 'retreat', 'ex_rule', 'ex_pokemon_down_description', 'illustrator',
                                                       'evolution_from', 'megaex_pokemon_down_description', 'megaex_pokemon_evolution_from', 'megaex_rule')}
        return dict(card=card, language=language, pokemon=pokemon, pokedex=pokedex, library_spec=localize(pokedex['SpecMSID']),
                    flavor=localize(card['FlavorTextMSID']), character=character, name=resolve_name(name_template, additional_names),
                    illustrators=[localize(k) for k in card['IllustratorNameMSIDs']], attacks=attack_records, abilities=ability_records, labels=labels,
                    name_template=name_template, additional_names=additional_names,
                    source_name_template=source_name, mega_source_name_template=mega_source_name,
                    source_name=resolve_name(source_name, additional_names), mega_source_name=resolve_name(mega_source_name, additional_names),
                    special_conditions=self.special_conditions(language), names=MessageNames(self.game, language))

    def trainer(self, card_id, language):
        table = self.game.table
        card = table('TrainerCard').one('CardID', card_id)
        trainer = table('Trainer').one('TrainerID', card['TrainerID'])
        character = table('Character').one('CharacterID', trainer['CharacterID'])
        proof = self.trainer_proof
        types = proof['trainerTypeTable']
        if trainer['TrainerType'] not in types['domainTypes']:
            raise ValueError('Unknown original trainer type')
        internal = types['internalTypes'][types['domainTypes'].index(trainer['TrainerType'])]
        files = self.game.locale(language)

        def localize(key, file='Master.bytes'):
            return files[file][key] if key else ''
        additional = self.scoped(language)['LocalizeMasterAdditionalName']['labels']
        name = localize(character['DisplayNameMSID'])
        sub = localize(card['RightEndDisplayNameMSID'])
        labels = {r['code']: localize(r['string']['Value'], 'UI.bytes') for r in proof['labels']}
        hp = 0
        if trainer['TrainerType'] == 4:
            try:
                hp = int(trainer['TrainerLogicParameters'][0])
            except (ValueError, IndexError):
                hp = 0  # Original GetFossilHp uses TryParse, default zero.
        return dict(kind='Trainers', language=language, card=card, trainer=trainer, character=character, trainerType=internal, hp=hp,
                    name=resolve_name(name, additional), name_template=name, sub_name=sub,
                    description_template=localize(trainer['DescriptionMSID']), illustrators=[localize(k) for k in card['IllustratorNameMSIDs']],
                    labels=dict(illustrator=labels[11], hp=labels[6], trainerRule=labels[{0: 8, 1: 9, 2: 10, 3: 7, 4: 8}[internal]], pokemonItemRule=labels[7]),
                    additional_names=additional, special_conditions=self.special_conditions(language), names=MessageNames(self.game, language))
