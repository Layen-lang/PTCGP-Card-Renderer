"""Lower original card UI conditions, language settings and font links for Unity."""
import json
import re
from .card_names import name_parts, resolve_name
from .paths import PACKAGE_DATA

TRAINER_UI_PROOF=json.loads((PACKAGE_DATA/'trainer-ui-branches-complete-source.json').read_text(encoding='utf-8'))
# LtUIImgTagCommand.PreProcessElement (1.7.5 RVA 0x4A20390), resolved
# through the original ELF relocations and ScriptString literals. These are
# Pokesymbol2 character codes, not the PokemonTypes enum values.
ELEMENT_GLYPHS = {
    'Grass': '\ue008', 'Fire': '\ue009', 'Water': '\ue00a',
    'Lightning': '\ue00b', 'Psychic': '\ue00c', 'Fighting': '\ue00d',
    'Colorless': '\ue00e', 'Darkness': '\ue00f', 'Metal': '\ue010',
    'Dragon': '\ue007', 'Fairy': '\ue005',
}
# Same method: the name attribute also accepts these letters, in either case.
ELEMENT_LETTERS = {
    'G': 'Grass', 'R': 'Fire', 'W': 'Water', 'L': 'Lightning', 'P': 'Psychic', 'F': 'Fighting',
    'C': 'Colorless', 'D': 'Darkness', 'M': 'Metal', 'N': 'Dragon', 'Y': 'Fairy',
}
# Literals of the original card text code; enum DamageSymbol Plus=1, Multiply=2, Minus=3.
DAMAGE_SIGNS = {0: '', 1: '<voffset=4>+</voffset>', 2: '<voffset=4>\u00d7</voffset>', 3: '<voffset=0>-</voffset>'}
# LtUICtrlTagCommand..ctor (1.7.5 RVA 0x4A1C180).
CIRCLED_NUMBERS = '\u2460\u2461\u2462\u2463\u2464\u2465\u2466\u2467\u2468\u2469'
# GetListItemStartString (1.8.1 RVA 0x4C8AC44): its language field stays 0 on cards, so the default
# entry applies. Read live on the real game in all 9 languages.
LIST_INDENT = '1.45em'
# LanguageType of the card assets (and LanguageCondition = 1<<value), not the protocol's enum where pt_BR=6.
LANGUAGE_TYPES = dict(ja_JP=0, en_US=1, fr_FR=2, it_IT=3, de_DE=4, es_ES=5, ko_KR=6, zh_TW=7, pt_BR=8)


def resolve_plain_tags(text):
    """Character and particle tags left in labels, flavor text and names."""
    out='';position=0
    for m in re.finditer(r'\[(C:Nbsp|C:Nnbsp|C:Nbh|C:Lsb|C:Rsb|Gr:Patchim)\b([^\]]*)\]',text):
        out+=text[position:m.start()];position=m.end()
        if m[1]=='Gr:Patchim':
            variant=re.search(r'v="([^"]*)"',m[2])[1]
            variant=PATCHIM_PRESETS.get(variant,variant)
            with_final,without=variant.split('/')
            out+=with_final if has_patchim(out,variant) else without
        else:
            out+=dict(zip(('C:Nbsp','C:Nnbsp','C:Nbh','C:Lsb','C:Rsb'),('\u00a0','\u202f','\u2011','[',']')))[m[1]]
    return out+text[position:]


def is_single(value, language):
    # MtUILocalizeNumHelper.IsSingle (RVA 0x5EF4064): French counts zero as singular.
    return abs(value)<2 if language=='fr_FR' else abs(value)==1
TEXT_TAG_ATTRIBUTES = {'id', 'ref', 'v', 'v0', 'v1', 'num_ref', 'style', 'suffix', 'plural'}
# MtUIGrTagCommand..ctor (1.8.1 RVA 0x5EF6E58): named Korean particle pairs.
PATCHIM_PRESETS = dict(eun_neun='은/는', eul_reul='을/를', i_ga='이/가', gwa_wa='과/와', eu_='으/', i_='이/', a_='아/야')
PRONUNCIATION = re.compile('\ue0ff(.*?)\ue0fe')


def has_patchim(text, variant):
    # MtUIGrTagCommand.IsPatchim (RVA 0x5EF7BC4): last syllable, skipping tags and punctuation.
    text = re.sub(r'<[^<>]*>', '', PRONUNCIATION.sub(r'\1', text))
    rieul = variant in ('으/', '으로/로', '으로서/로서')
    for char in reversed(text):
        if char.isascii() and char.isalpha():
            return False
        if '가' <= char <= '힣':
            final = (ord(char)-0xAC00)%28
            return final != 0 and not (rieul and final == 8)
        if char.isdigit():
            return char in ('036' if rieul else '013678')
    return False


def build_ui_plan(source,layout,card,settings_blob,design_name=None):
    language_code=card.get('language','fr_FR')
    language=LANGUAGE_TYPES[language_code]
    language_flag=1 << language
    trainer=card.get('kind')=='Trainers'
    is_dual=False if trainer else len(card['pokemon']['PokemonTypes'])>1
    is_rgb=False if trainer else card['card']['Rarity']==1000
    trainer_ui_proof=TRAINER_UI_PROOF if trainer else None
    mega=False if trainer else bool(card['pokemon'].get('IsMegaEvolution',False))
    # Original CategoryType: UltraBeast=1, Ancient=2, Future=3.
    categories=set(card['trainer' if trainer else 'pokemon'].get('AdditionalCategories',[]))
    if categories - {1,2,3}: raise ValueError(f'Unknown Pokemon categories: {categories}')
    abilities=card.get('abilities',[])
    if len(abilities)>1: raise ValueError('More than one ability is not supported')
    components={int(c['id']):c for c in layout['components']}
    ui_behaviour=next(c for c in layout['components'] if c['type']==('Trainers' if trainer else 'Pokemon')+'CardUIBehaviour')
    # CardUIBehaviour applies Setup only to this serialized list. The three
    # additional-name configs exist on the prefab but are not registered;
    # their TMP defaults determine the width used by LayoutExBetweenTexts.
    configured_texts={p['m_PathID'] for p in ui_behaviour['fields']['_cardTextConfigs']}
    transforms={int(n['transform_id']):n for n in layout['nodes']}
    gameobjects={int(n['gameobject_id']):n for n in layout['nodes']}
    multi=[c for c in layout['components'] if c['type']=='CardMultiView']
    def attack_index(c):
        parents=[m for m in multi if c['indices'][:len(m['indices'])]==m['indices']]
        if len(parents)!=1:
            raise ValueError(f'Expected one CardMultiView ancestor: {c["path"]}')
        return parents[0]['fields']['_index']
    def target(pointer):
        return (components.get(pointer['m_PathID']) or transforms[pointer['m_PathID']])['indices']
    tree,asset=source.tree,source.asset

    settings=source.card_settings(card['card']['CardID'],settings_blob)
    design=source.tree({'m_PathID':source.named('MonoBehaviour',design_name)['id']}) if design_name else tree(settings['_cardDesignSettings'])
    groups=tree(design['_fonts'])
    if trainer:
        if groups['_conditions']:raise ValueError('Trainer font-group conditions require their original branch to be ported')
        selected_group=groups['_default']
    else:
        def matches_font(c):
            flags=c['_conditionType']
            return (flags!=0 and (not flags&1 or c['_energyType']==settings['_energyType'])
                    and (not flags&2 or bool(c.get('_isDual',False))==is_dual))
        selected_group=next((c['_font'] for c in groups['_conditions'] if matches_font(c)),groups['_default'])
    group=tree(selected_group)
    fonts={f['_key']:tree(f['_font']) for f in group['_fonts']}
    default=tree(group['_default']) if group['_default']['m_PathID'] else None
    values={0:', '.join(card['illustrators']),1:card['labels']['illustrator'],117:card['labels']['hp']}
    if not trainer:
        values.update({101:str(card['pokemon']['HP']),115:card['labels']['weakness'],116:card['labels']['retreat']})
    # GetWeaknessOperand (RVA 0x44ef690) returns 20. CreatePokemonCardData
    # passes it to PokemonWeaknessData; UpdatePokemonCardTextViews emits
    # keys 112/113 only when the nullable weakness energy is present.
        values.update({112:'20' if card['pokemon']['WeaknessType'] else '',113:'+' if card['pokemon']['WeaknessType'] else ''})
    # Library, flavor, evolution and ability come from the selected master
    # record. Empty source strings stay empty rather than borrowing RR data.
    # UpdateTextView key102 sets LocalizedTagParam.PlainTextMode=true
    # (1.7.5 RVA 0x47F7488); evolution names keep uniform typography.
    source_name=resolve_name(card.get('source_name_template',card.get('source_name','')),card.get('additional_names',{}))
    stage_spec=re.sub(r'\[Mst:CardCharacterName\s*\]',lambda _:source_name,card['labels'].get('evolution_from','')) if source_name else ''
    # The original RR card data hides the library/flavor for Mega cards.
    if trainer:
        name,additional_name=card['name'],''
        values.update({200:name,201:card['sub_name'],101:str(card['hp']),204:card['labels']['trainerRule'],
            # ValueConverter.ToInternal (RVA 0x481d810): domain 600 -> SR=5.
            202:chr(226 if card['card']['Rarity']==600 else 210)+card['labels']['pokemonItemRule']})
    else:
        values.update({102:stage_spec,103:'' if mega else card['library_spec'],104:'' if mega else card['flavor'],105:abilities[0]['name'] if abilities else '',106:''})
        name,additional_name=name_parts(card.get('name_template',card['name']),card.get('additional_names',{}),card['pokemon']['IsEX'],language_code)
        values.update({119:'',120:''})
    if mega:
        mega_spec=re.sub(r'\[C:Nbsp\s*\]','\u00a0',re.sub(r'\[Mst:CardCharacterName\s*\]',lambda _:card['mega_source_name'],card['labels']['megaex_pokemon_evolution_from']))
        values.update({118:name,119:mega_spec,120:mega_spec})
    # CreatePokemonCardData constructs SerialNumberData(None=1,999,999).
    # CardUIBehaviour.UpdateTextView returns without assigning serial keys
    # when Type=None; all serial containers must remain inactive.
    plan=dict(language=language,stage=0 if trainer else card['pokemon']['EvolutionStage'],rule=1 if trainer else 2 if card['pokemon']['IsEX'] else 1,serial=1,isMegaEvolution=int(mega),
              isTrainer=trainer,trainersType=card['trainerType'] if trainer else 0,hasSubType=int(bool(card.get('sub_name',''))),
              conditionSchema=181 if any('_isRGBFormat' in a for c in layout['components'] if c['type']=='CardConditionView' for a in c['fields']['_conditions']) else 175,
              isDual=int(is_dual),isRGBFormat=int(is_rgb),
              isUltraBeast=int(1 in categories),isAncient=int(2 in categories),isFuture=int(3 in categories),
              serialNumerator=999,serialDenominator=999,nativeSkippedKeys=[2,3,4],
              conditions=[],settings=[],texts=[],unportedKeys=[],containers=[],energyIcons=[],names=[],emptySprites=[],sprites=[],skillPadding=[],skillSpacing=[],textActivations=[],fontReferences=[],dynamicViews=[])
    # CardDynamicUIView.Apply (1.7.5 RVA 0x47E10EC) enables each direct
    # child whose name matches the design's selected GameObject; null hides all.
    for choice in design['_dynamicUIs']:
        key=components[choice['_key']['m_PathID']]
        if key['type']!='CardDynamicUIView': raise ValueError('Unexpected dynamic UI key')
        selected=tree(choice['_gameObject'])['m_Name'] if choice['_gameObject']['m_PathID'] else None
        for view in layout['components']:
            if view['type']=='CardDynamicUIView' and view['fields']['_label']==key['fields']['_label']:
                children=[n for n in layout['nodes'] if n['indices'][:-1]==view['indices'] and len(n['indices'])==len(view['indices'])+1]
                if selected is not None and not any(n['path'].rsplit('/',1)[-1]==selected for n in children):
                    raise ValueError(f'Dynamic UI child absent: {selected}')
                plan['dynamicViews'].append(dict(indices=view['indices'],label=key['fields']['_label'],selectedName=selected))
    def bold_of(key):
        # Conditions of text type 2, as for the rule text below.
        bold=next((t for t in fonts.get(key,default)['_textTypeFontConditions'] if t['_type']==2),None)
        # PreProcessForTMP (RVA 0x4C88F00) adds no tag when the view has no bold font.
        if bold is None: return None
        conditions=bold['_conditions']
        chosen=next((t for t in conditions if not t['_isDefault'] and t['_language']==language),None) or next(t for t in conditions if t['_isDefault'])
        return asset(chosen['_font']['_font']),asset(chosen['_font']['_material'])
    def description(record,size,ex_size,key):
        # LtUIImgTagCommand's default is 23 when the view leaves the override
        # at zero; verified in the original Mega Lucario UI via ADB.
        if size<=0: size=23
        params=record['record'].get('AttackLogicParameters',record['record'].get('AbilityLogicParameters',record['record'].get('TrainerLogicParameters',[])))
        list_items=[0]
        def expand(text):
            # [[ and ]] are literal brackets.
            out=''; position=0
            for m in re.finditer(r'\[\[|\]\]|\[([^\[\]]+)\]',text):
                out+=text[position:m.start()]; position=m.end()
                out+=tag(m,out) if m[1] else m[0][0]
            return out+text[position:]
        def tag(m,current):
            token=m[1].strip()
            command=token.split(maxsplit=1)[0]
            attributes=dict(re.findall(r'(\w+)\s*=\s*"([^"]*)"',token))
            # id/ref are message-parser metadata, not part of the command name.
            if command.startswith('Text:') and command!='Text:AdditionalName' and set(attributes)-TEXT_TAG_ATTRIBUTES:
                # ConvertFirstCharCase and ConvertSpace are not ported.
                raise ValueError(f'Unsupported text tag attribute: [{token}]')
            def text_value():
                # ResolveTextTag prefers v, otherwise the logic parameter.
                return attributes.get('v') or params[int(attributes.get('id','0'))]
            names=card['names']
            # MtUI/LtUICtrlTagCommand.PreProcessForTMP (RVA 0x4A1D780).
            if command=='Gr:Pron': return f'\ue0ff{attributes["v"]}\ue0fe'
            if command=='Gr:Patchim':
                if 'omit' in attributes: raise ValueError(f'Unsupported particle omission: [{token}]')
                variant=PATCHIM_PRESETS.get(attributes['v'],attributes['v'])
                with_final,without=variant.split('/')
                return with_final if has_patchim(current,variant) else without
            if command=='C:Nnbsp': return '\u202f'
            if command=='C:Lsb': return '['
            if command=='C:Rsb': return ']'
            if command=='Ctrl:Bold':
                if bold_of(key) is None: return ''
                bold_font,bold_material=bold_of(key)
                reference=dict(font=bold_font,material=bold_material)
                if reference not in plan['fontReferences']: plan['fontReferences'].append(reference)
                return f'<font="{bold_font["name"]}" material="{bold_material["name"]}">'
            if command=='/Ctrl:Bold': return '</font>' if bold_of(key) else ''
            if command=='Ctrl:NoBreak': return '<nobr>'
            if command=='/Ctrl:NoBreak': return '</nobr>'
            if command=='Ctrl:OL':
                list_items[0]=0
                return ''
            if command=='/Ctrl:OL': return ''
            if command=='Ctrl:LI':
                list_items[0]+=1
                if list_items[0]>len(CIRCLED_NUMBERS): raise ValueError('Ordered list too long')
                return f'{CIRCLED_NUMBERS[list_items[0]-1]}<indent={LIST_INDENT}>'
            if command=='/Ctrl:LI': return '</indent>'
            if command=='Text:AdditionalName':
                return resolve_name(f'[{token}]',card['additional_names'],rich=True,language=language_code)
            if command=='Text:CardName': return expand(names.character(text_value()))
            if command=='Text:CardNameByCardID': return expand(names.card(text_value()))
            if command=='Text:AbilityName': return expand(names.ability(text_value(),attributes.get('suffix')))
            if command=='Text:EvolutionPokemon':
                value=expand(names.evolution_pokemon(text_value()))
                return value.replace('<b>','').replace('</b>','') if attributes.get('style')=='plain' else value
            if command=='C:Nbsp': return '\u00a0'
            if command=='C:Nbh': return '\u2011'
            # Original Stadium template and confirmed game TMP string:
            # Ctrl:Italic wraps precisely this span in <i>...</i>.
            if command=='Ctrl:Italic': return '<i>'
            if command=='/Ctrl:Italic': return '</i>'
            if command=='Img:ex':
                # PreProcessEX: Black/White/BlackWithWhiteOutline -> 0/2/1.
                # The design selects the style; CardTextView supplies its size.
                img_type=group['_imgTagFontType']
                if img_type not in (1,2,3): raise ValueError(f'Unknown ex tag font type: {img_type}')
                resolved_size=ex_size if ex_size>0 else 23
                return f'<space=-0.01em><size={resolved_size:g}><sprite={ {1:0,2:2,3:1}[img_type] }><space=-0.1em></size>'
            if command=='Num:Int':
                value=params[int(attributes.get('id','0'))]
                # MtUILocalizeNumHelper.IsVisible (RVA 0x5EF4308).
                if 'visible' in attributes and attributes['visible'].strip()!='1': return ''
                # MtUINumTagCommand.PreProcess (RVA 0x5BFF2AC): plural_only is
                # appended to the number and both vanish for a single item.
                if 'plural_only' not in attributes: return value
                return '' if is_single(int(value),language_code) else value+attributes['plural_only']
            if command=='Gr:Count':
                # MtUIGrTagCommand.PreProcess (RVA 0x5EF76E0): optional forms for exactly two and for one.
                count=int(params[int(attributes.get('ref','0'))])
                if abs(count)==2 and 'two' in attributes: return attributes['two']
                if is_single(count,language_code): return attributes.get('one',attributes.get('s',''))
                return attributes.get('p','')
            if command=='Text:SpecialCondition':
                condition=params[int(attributes.get('id','0'))]
                # ResolveSpecialConditionTag uses the scoped SpecialConditionType
                # labels, not the flattened snapshot where duplicate keys collide.
                number='plural' if attributes.get('plural','0')=='1' else 'singular'
                labels=card['special_conditions'][number]
                if condition not in labels: raise ValueError(f'Unknown special condition: {condition}')
                # The label itself holds tags in some languages.
                value=expand(labels[condition])
                # ResolveSpecialConditionTag: German word order depends on the condition.
                if attributes.get('v0') and attributes.get('v1'):
                    value=attributes['v1' if condition=='Asleep' else 'v0'].replace('{}',value)
                if attributes.get('style')=='plain': value=value.replace('<b>','').replace('</b>','')
                return value
            if command=='Text:AttackName':
                # ResolveAttackNameTag 496ee48 -> slot 10 GetPokemonAttackNameText
                # 4970aac: lookup PokemonAttackNameID and resolve NameMSID.
                return expand(names.attack(text_value()))
            if command=='Img:Element':
                # PreProcessElement reads name first, then the id parameter.
                element=attributes.get('name') or params[int(attributes['id'])]
                element=ELEMENT_LETTERS.get(element.upper(),element) if len(element)==1 else element[0].upper()+element[1:]
                if element not in ELEMENT_GLYPHS: raise ValueError(f'Unknown element glyph: {element}')
                symbol=asset({'m_PathID':1866416259400829261})
                symbol_tree=tree({'m_PathID':1866416259400829261})
                img_type=group['_imgTagFontType']
                if img_type==1:
                    material=asset(symbol_tree['m_Material'])
                elif img_type in (2,3):
                    material_name={2:'Pokesymbol2-regular-SDF-White',3:'Pokesymbol2-regular-SDF-WhiteOutline'}[img_type]
                    material_obj=source.named('Material',material_name)
                    material=source.asset({'m_PathID':material_obj['id']})
                else: raise ValueError(f'Unknown element tag font type: {img_type}')
                reference=dict(font=symbol,material=material)
                if reference not in plan['fontReferences']: plan['fontReferences'].append(reference)
                return f'<size={size:g}><font="{symbol["name"]}" material="{material["name"]}">{ELEMENT_GLYPHS[element]}</font></size>'
            raise ValueError(f'Unsupported message tag: [{token}]')
        # The real Sac Évoli shows one blank line before its list, not two.
        return PRONUNCIATION.sub('',expand(record['description_template'].replace('[Ctrl:OL]\n','[Ctrl:OL]')))
    container=None if trainer else 0 if abilities and len(card['attacks'])==1 else len(card['attacks']) if not abilities and len(card['attacks']) in (1,2) else 3
    for c in layout['components']:
        f=c['fields']
        if c['type']=='CardSkillView':
            count=max(1,max(len(a['record']['AttackCost']) for a in card['attacks']))
            plan['skillPadding'].append(dict(indices=target(f['_horizontalLayoutGroup']),
                value=f['_lEqualPadding'] if count<=f['_paddingSizeThreshold'] else f['_greaterPadding']))
        if c['type']=='CardSkillContainerView':
            plan['skillSpacing'].append(dict(indices=target(f['_verticalLayoutGroup']),
                threshold=f['_spaceSizeThreshold'],lessEqual=f['_lEqualSpacing'],greater=f['_greaterSpacing']))
        if c['type']=='CardSpriteView' and f['_key']==1:
            if source_name:
                sprite=source.named('Sprite',card['pokemon']['PreevolvedCharacterID'])
                plan['sprites'].append(dict(indices=target(f['_image']),sprite=source.asset({'m_PathID':sprite['id']})))
            else: plan['emptySprites'].append(dict(indices=target(f['_image'])))
        if c['type']=='CardContainerView':
            plan['containers'].append(dict(indices=c['indices'],active=f['_key']==container))
        if c['type']=='CardEnergyView':
            key=f['_key']
            energies={0:card['pokemon']['PokemonTypes'],1:[card['pokemon']['WeaknessType']] if card['pokemon']['WeaknessType'] else [],
                      2:[1]*card['pokemon']['RetreatAmount']}.get(key)
            if key==3:
                index=attack_index(c)
                energies=card['attacks'][index]['record']['AttackCost'] if index<len(card['attacks']) else []
            if energies is None:
                raise ValueError(f'Unmapped energy key {key}')
            fields={1:'_colorless',2:'_grass',3:'_fire',4:'_water',5:'_lightning',6:'_psychic',7:'_fighting',8:'_darkness',9:'_metal',10:'_dragon'}
            for i,pointer in enumerate(f['_energies']):
                icon=components[pointer['m_PathID']]
                view=icon['fields']
                active=i<len(energies)
                plan['energyIcons'].append(dict(indices=icon['indices'],active=active,
                    imageIndices=target(view['_imgEnergy']),
                    sprite=asset(view[fields[energies[i]]]) if active else None))
        if c['type']=='CardConditionView':
            plan['conditions'].append(dict(indices=c['indices'],alternatives=f['_conditions']))
        if c['type']=='CardTextConfig' and int(c['id']) in configured_texts:
            parameters=next((p for p in f['_parameters'] if p['_language']&language_flag),None)
            plan['settings'].append(dict(indices=c['indices'],parameters=parameters,shrink=f['_shrinkSetting']))
        if c['type'] in ('CardTextView','PokemonCardNameView','PokemonCardMegaEvolutionFromNameView'):
            key=f['_key']
            localvalues=dict(values)
            if trainer and key==203:
                localvalues[key]=description(dict(record=card['trainer'],description_template=card['description_template']),f['_elementTagFontSize'],f['_exTagFontSize'],key)
            if key in (107,108,109,110):
                index=attack_index(c)
                if index<len(card['attacks']):
                    attack=card['attacks'][index]
                    symbol=attack['record']['DamageSymbol']
                    if symbol not in DAMAGE_SIGNS: raise ValueError(f'Unported damage symbol: {symbol}')
                    localvalues.update({107:attack['name'],108:description(attack,f['_elementTagFontSize'],f['_exTagFontSize'],key),109:str(attack['record']['Damage']) if attack['record']['Damage']>0 else '',110:DAMAGE_SIGNS[symbol]})
                else: localvalues.update({107:'',108:'',109:'',110:''})
            if key==106 and abilities: localvalues[106]=description(abilities[0],f['_elementTagFontSize'],f['_exTagFontSize'],key)
            if c['type']=='PokemonCardNameView':
                # Native s_exRegex from InitializePokemonNamePatterns, via script.json.
                localvalues[key]=name
                max_name_width=f['_textMaxWidthForEx']
                if is_dual and '_textMaxWidthForDual' in f:
                    # 1.8.1 UpdateExLayout (RVA 0x4A5C8A0) first caps at
                    # +0x50 (EX), then at +0x54 when PokemonCardData.IsDual.
                    max_name_width=min(max_name_width,f['_textMaxWidthForDual'])
                plan['names'].append(dict(indices=c['indices'],exIndices=target(f['_ex']),
                    maxWidth=max_name_width,active=f['_lineType']==0,isEx=card['pokemon']['IsEX'],
                    splitEx=bool(additional_name),additionalIndices=target(f['_additionalText']),
                    maxSplitWidth=f['_textMaxWidthForRocketPt'],exWidth=f['_exWidth'],minWidth=f['_minWidth'],
                    spaceLeftCenter=f['_spaceLeftCenter'],spaceCenterRight=f['_spaceCenterRight']))
            font=fonts.get(key,default)
            if font is None:raise ValueError(f'Original font absent for text key {key}; no replacement font is permitted')
            types=font['_textTypeFontConditions']
            defaulttype=next((t for t in types if t['_type']==1),None)
            if defaulttype is None:
                raise ValueError(f'No default text font for key {key}')
            conditions=defaulttype['_conditions']
            selected=next((t for t in conditions if not t['_isDefault'] and t['_language']==language),None)
            if selected is None:
                selected=next(t for t in conditions if t['_isDefault'])
            textsource=next(t for t in layout['components'] if t['type']=='TextMeshProUGUI' and t['indices']==c['target_text']['indices'])
            rich={}
            if key in (106,108,203) and '<sprite=' in localvalues.get(key,''):
                rich['spriteAsset']=asset({'m_PathID':840073264968542736})
            if key in (111,121):
                boldconditions=next(t for t in types if t['_type']==2)['_conditions']
                bold=next((t for t in boldconditions if not t['_isDefault'] and t['_language']==language),None)
                if bold is None:
                    bold=next(t for t in boldconditions if t['_isDefault'])
                boldfont=asset(bold['_font']['_font'])
                boldmaterial=asset(bold['_font']['_material'])
                value=card['labels']['megaex_pokemon_down_description' if key==121 else 'ex_pokemon_down_description']
                # UpdatePokemonCardTextViews selects ExBlack=4 and copies _exTagFontSize.
                # PreProcessEX maps ExBlack to character index 3.
                size=f['_exTagFontSize']
                assert size>0
                value=re.sub(r'\[Img:ex\s*\]',f'<space=-0.01em><size={size:g}><sprite={2 if key==121 else 3}><space=-0.1em></size>',value)
                value=re.sub(r'\[C:Nbh\s*\]','\u2011',value)
                value=re.sub(r'\[C:Nbsp\s*\]','\u00a0',value)
                value=re.sub(r'\[Ctrl:Bold(?: id="\d+")?\s*\]',
                    lambda m:f'<font="{boldfont["name"]}" material="{boldmaterial["name"]}">',value)
                value=value.replace('[/Ctrl:Bold]','</font>')
                # ResolveChar -> GetNarrowSpaceChar (RVA 0x496FF58): TMP at 100% is a plain space.
                value=re.sub(r'\[Text:Char v="THREE-PER-EM-SPACE"\s*\]',' ',value)
                if '[' in value: raise ValueError('Unsupported rule markup')
                localvalues[key]=value
                rich=dict(boldFont=boldfont,boldMaterial=boldmaterial,
                    spriteAsset=asset({'m_PathID':840073264968542736}))
            wrapping=textsource['fields']['m_TextWrappingMode']
            if wrapping not in (0,1):
                raise ValueError(f'Unported whitespace mode {wrapping}')
            if trainer and key==204 and card['trainerType']==2:
                auto=next((v for v in f['_autoSizeConditions'] if v['_language']&language_flag),None)
                if auto is not None:
                    # Original UpdateTextView applies AutoMinScale to every
                    # matching key-204 view; runtime formats the original float.
                    rich.update(scaleValue=auto['_autoMinScale'],applyScale=True,scaleFormat=trainer_ui_proof['scaleFormat'])
            # UpdateSkillText / TrainersCardUIBehaviour.UpdateTextView: views taking part in line fitting.
            # Slot 1 is the ability or the trainer text, slot n+2 attack n.
            line_slot=0
            if key==203 or key==106 and abilities: line_slot=1
            elif key==108 and attack_index(c)<len(card['attacks']): line_slot=attack_index(c)+2
            fitting=next((v for v in f.get('_autoSizeConditions',[]) if v['_language']&language_flag),None) if line_slot else None
            if line_slot: rich.update(lineSlot=line_slot)
            if fitting: rich.update(autoMinScale=fitting['_autoMinScale'],autoMinSize=fitting['_autoMinSize'])
            plan['texts'].append(dict(indices=c['target_text']['indices'],key=key,wrap=wrapping==1,
                 font=asset(selected['_font']['_font']),material=asset(selected['_font']['_material']),
                 additionalIndices=target(f['_additionalText']) if c['type']=='PokemonCardNameView' else None,
                 additionalValue=additional_name if c['type']=='PokemonCardNameView' else None,
                 setValue=key in localvalues,value=resolve_plain_tags(localvalues.get(key,'')),**rich))
            if key in localvalues and c['type']=='CardTextView':
                for pointer in f['_inactivateOnEmpty']:
                    # CardTextView.SetText calls SetActive(!String.IsNullOrEmpty)
                    # on each explicitly referenced object, not on TMP itself.
                    plan['textActivations'].append(dict(indices=gameobjects[pointer['m_PathID']]['indices'],
                        active=bool(localvalues[key]),key=key,path=gameobjects[pointer['m_PathID']]['path']))
            if key not in localvalues and key not in plan['nativeSkippedKeys']:
                plan['unportedKeys'].append(key)
    plan['unportedKeys']=sorted(set(plan['unportedKeys']))
    plan['status']='Original card source data and design UI mapped; visual fidelity requires per-card GPU/reference verification'
    return plan
