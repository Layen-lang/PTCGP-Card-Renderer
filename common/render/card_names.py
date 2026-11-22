"""Expand original AdditionalName labels for plain text and card TMP."""
import re

EX_SUFFIX = re.compile(r'\s?-?ex$')
EX_SPLIT = re.compile(r'(?<=(?:\s|-)ex)(?=[^\w\s])')


CJK_LANGUAGES = {'ja_JP', 'ko_KR', 'zh_TW'}


def additional_size(kind, language):
    """ResolveAdditionalName (1.8.1 RVA 0x4BCDBA8): percent size, or None for no size tag."""
    if kind in ('A', 'C', 'region'):
        return 74 if language in CJK_LANGUAGES else 70
    return None if language in CJK_LANGUAGES else 70


def resolve_name(template, labels, rich=False, language='fr_FR'):
    def tag(match):
        token = match[1].strip()
        command = token.split(maxsplit=1)[0]
        attributes = dict(re.findall(r'(\w+)="([^"]*)"', token))
        if command == 'C:Nbsp': return ' '
        if command == 'C:Nbh': return '‑'
        if command == 'C:Nnbsp': return '\u202f'
        if command == 'C:Lsb': return '['
        if command == 'C:Rsb': return ']'
        # Pronunciation hint for Korean particles, never displayed.
        if command == 'Gr:Pron': return ''
        if command != 'Text:AdditionalName':
            raise ValueError(f'Unsupported Pokemon name tag: [{token}]')
        value = labels[attributes['v']]
        kind = attributes.get('type', '')
        if kind not in ('A', 'B', 'C', 'region', 'origin'):
            raise ValueError(f'Unknown additional name type: {kind}')
        size = additional_size(kind, language)
        if rich and size:
            value = f'<size={size}%>{value}</size>'
        space = '<size=75%> </size>' if rich else ' '
        spacing = attributes.get('space', '')
        if spacing == 'pre': return space + value
        if spacing == 'post': return value + space
        if not spacing or spacing == 'none': return value
        raise ValueError(f'Unknown additional name spacing: {spacing}')
    return re.sub(r'\[([^\]]+)\]', tag, template)


def name_parts(template, labels, is_ex, language='fr_FR'):
    resolved = resolve_name(template, labels, rich=True, language=language)
    # French Team Rocket ex names put the suffix after the ex sprite.
    parts = EX_SPLIT.split(resolved, maxsplit=1) if is_ex else [resolved]
    return EX_SUFFIX.sub('', parts[0]) if is_ex else parts[0], parts[1] if len(parts) == 2 else ''
