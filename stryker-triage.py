"""Triage a Stryker report into decide-about / drop buckets.

Ranks by FAILURE-MODE SILENCE, not by count: a survivor matters when the wrong behaviour it
describes would ship without anyone noticing. The output is a list to decide about once, not a
score to chase.

Usage: triage.py <report.json> [out.md]
"""
import json, io, os, re, sys, collections

REPORT = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else None

# ── Concern map: what a wrong answer in this file COSTS, in CLAUDE.md's own terms. ──────────────
# A  silent AND harmful: the client/operator cannot tell it went wrong.
# B  wrong but INSPECTABLE: wrong document or wrong refusal, recoverable and visible.
# C  loud or inert: it throws, it is logged, or nothing observable changes.
CONCERN = {
    # A -- disclosure boundaries. A wrong answer SERVES data that should be withheld.
    'IgnoredPropertyJsonOptions.cs': ('A', 'disclosure: which property names are withheld from the wire'),
    'OhDataAuthRequirementsText.cs': ('A', 'disclosure: which auth detail reaches a generated document'),
    # B, not A: #481's ENFORCEMENT was refused by owner ruling, so this only emits a Warning.
    # A wrong answer is a missing or spurious diagnostic, not a bypass.
    'NavigationTargetAuthorization.cs': ('B', '#481 cross-navigation auth WARNING (diagnostic, not a gate)'),
    'InheritedTypeConfig.cs': ('A', 'disclosure: withheld-name resolution up the base chain'),
    'OpenTypeJsonOptions.cs': ('A', 'disclosure + write-path validation over dynamic keys'),
    'ModelBoundAllowlists.cs': ('A', '#458: two profiles over one CLR type must not union allowlists'),
    'EdmClrTypeMap.cs': ('A', '#508: CLR->EDM resolution the suppression map depends on'),

    # A -- concurrency and identity primitives. A wrong answer corrupts or loses data silently.
    'ETagValueFormatter.cs': ('A', 'concurrency: a collision makes If-Match a silent no-op'),
    'ODataKeyParser.cs': ('A', 'identity: the key a request addresses'),
    'ODataEntityKeyUrlFormatter.cs': ('A', 'identity: entity-id URLs a client GETs back'),
    'CapturedState.cs': ('A', 'lifetime: a stale/disposed scoped dependency per request'),
    'DeltaExtensions.cs': ('A', 'write path: which properties a Delta reports as changed'),
    'DeltaExpressionHelper.cs': ('A', 'write path: selector -> property name'),

    # B -- advertise-vs-serve and refusal shape. Wrong, but inspectable.
    'OhDataQueryOptionsMetadata.cs': ('B', 'advertises which options a route honours'),
    'OhDataApiDescriptionProvider.cs': ('B', 'OpenAPI description surface'),
    'SchemaPropertyCasing.cs': ('B', 'schema names must match what the serializer emits'),
    'ODataMaxVersionFilter.cs': ('B', 'OData-MaxVersion refusal (§8.2.7)'),
    'ExceptionMapping.cs': ('B', 'ConfigureExceptions status mapping'),
    'OperationSignatureValidation.cs': ('B', '#498 bind-time refusals'),
    'BoundOperationDefinition.cs': ('B', 'operation shape feeding EDM + dispatch'),
    'UnboundOperationDefinition.cs': ('B', 'operation shape feeding EDM + dispatch'),
    'AsyncDispatchHelper.cs': ('B', 'return-type unwrapping for dispatch'),
    'ProfileScanner.cs': ('B', 'which profiles are discovered'),
    'ODataPropertyNaming.cs': ('B', 'EDM<->CLR name resolution'),
    'EntitySetDefaults.cs': ('B', 'server-wide defaults'),
    'OhDataBuilder.cs': ('B', 'registration + startup validation'),
    'OhDataEndpointFactory.cs': ('B', 'the route table and every pipeline stage'),
    'EntitySetProfile.cs': ('B', 'the profile surface'),
    # A -- the mapper's read path. A wrong answer here serves WRONG ROWS or wrong values.
    'ModelToEntityRewriter.cs': ('A', 'substitutes model terms into the entity predicate -- wrong = wrong rows'),
    'MapExpressions.cs': ('A', 'BindingFor/GuardAndNarrow: projection and predicate must agree'),
    'MappedQueryComposer.cs': ('A', 'composes the SQL the page is read from'),
    'MappedNavigationLoader.cs': ('A', 'serves $expand rows for a mapped set'),
    'MappedEntitySetProfile.cs': ('A', 'P1 profile: owns $filter/$orderby/$top/$skip and paging'),
    'MappedNextLink.cs': ('A', 'continuation offset -- a wrong link silently skips or repeats rows'),
    'DeltaFactory.cs': ('A', 'write path: #479/#488 failure mode is 200 with nothing persisted'),
    'ModelProjection.cs': ('A', 'the scalar projection each row is built from'),

    # B -- declaration/registration/startup. Wrong is loud or inspectable.
    'ModelMapValidator.cs': ('B', 'startup refusals for an ill-formed map'),
    'ModelMap.cs': ('B', 'the mapping vocabulary'),
    'ModelMapRegistry.cs': ('B', 'map lookup'),
    'ModelMemberBinding.cs': ('B', 'binding kinds'),
    'MappedProfileBuilder.cs': ('B', 'profile construction'),
    'DeltaProfile.cs': ('B', 'delta mapping declaration'),
    'DeltaProfileRegistration.cs': ('B', 'DI registration'),

}

# ── Core package, classified from source (not from the filename) before the artifact ships. ────
CONCERN.update({
    # A -- authorization. A wrong answer silently authorizes or un-gates.
    'OperationAuthorization.cs': ('A', 'auth category flags: a dropped bit un-gates a verb (Write = Create|Update|Delete)'),
    'AuthorizationRuleBuilder.cs': ('A', 'turns a declaration into a rule -- #487 seam (2) is the no-requirement fail-open'),
    'OhDataOperations.cs': ('A', 'resource-auth requirement identity: a collision lets one category satisfy another'),

    # A -- disclosure.
    'IgnoredPropertyDocsMap.cs': ('A', 'disclosure: the withheld names the companion packages omit from public schemas'),

    # A -- silently wrong ROWS or VALUES on the wire.
    'OhDataSystemQueryOption.cs': ('A', '#475 HonouredQueryOptions default: wrongly honouring $search serves the FULL collection'),
    'ODataEntitySetProfile.cs': ('A', 'P1 seam: HonouredQueryOptions + the result projection the framework re-envelopes'),
    'ActionBodySchemaTypeFactory.cs': ('A', 'action parameter names: a wrong name binds null under a 200'),
    'RoundingMode.cs': ('A', '#100 rounding mode -- silently wrong numbers'),

    # B -- wrong but inspectable: refusal shape, registration state, declared surface.
    'OhDataResult.cs': ('B', 'the rejection status/code a client receives'),
    'OhDataRegistration.cs': ('B', 'registration state: prefix, OpenTypesActive, body-size default'),
    'OhDataRegistrationCollection.cs': ('B', 'registration lookup'),
    'IEntitySetEndpointSource.cs': ('B', 'the runtime-typed profile surface (#492 HasCollectionGet)'),
    'IEntitySetProfile.cs': ('B', 'declaration only'),
    'IODataEntitySetEndpointSource.cs': ('B', 'declaration only'),
    'IOhDataStartupValidated.cs': ('B', '#665 startup-validation marker'),
    'IVisitModelBuilder.cs': ('B', 'declaration only'),
    'NavigationRouteDefinition.cs': ('B', 'nav route shape feeding the #380 two-branch gate'),
    'StructuralPropertyInfo.cs': ('B', 'structural property descriptor'),
    'OhDataAnonymousRouteAudit.cs': ('B', '#487 audit record'),
    'OhDataOperationAuthMetadata.cs': ('B', 'carries requirements to the endpoint gate'),
    'OhDataExceptionContext.cs': ('B', 'ConfigureExceptions context'),
    'OhDataDiagnostics.cs': ('B', '#200 telemetry identifiers -- a dashboard contract'),
    'ODataQueryResult.cs': ('B', 'P1 paging metadata carrier'),
    'ODataCollectionResponse.cs': ('B', 'documentation envelope shape'),
    'ODataDocumentationResponses.cs': ('B', 'documentation envelope shapes'),
    'ODataDocumentationRequests.cs': ('B', 'documentation request shapes'),
    'HandlerFaultException.cs': ('B', '#496 user-code fault marker -- a wrong unwrap moves a 500 to a 400'),
    'OhDataRejectionException.cs': ('B', 'rejection transport'),
    'ODataKeyFormatException.cs': ('B', '#496: the ONE type the eighteen BadKeyError clauses catch'),
    'ServiceCollectionExtensions.cs': ('B', 'AddOhData entry point'),
    'ServiceCollectionVersioningExtensions.cs': ('B', 'AddOhDataVersion entry point'),
    'EndpointRouteBuilderExtensions.cs': ('B', 'MapOhData entry point'),
    'EndpointRouteBuilderVersioningExtensions.cs': ('B', 'MapOhDataVersion entry point'),

    # C -- provably inert: one const, read by both sides of the comparison it keys.
    'OhDataDefaults.cs': ('C', 'the __default__ key: AddOhData and MapOhData read the SAME const, so a mutation moves both'),
})

# ── Mutation shapes that are LOUD or INERT regardless of file. ─────────────────────────────────
# A line whose job is to BUILD a message: an exception, a log call, or an accumulated
# validation error. Wrong is loud or inspectable whatever the mutator does to it.
PROSE = re.compile(r'throw new|Log(Warning|Debug|Error|Information|Trace|Critical)\(|'
                   r'errors\.Add\(|^\s*"[A-Z][^"]{25,}"|Exception\(')
# A string that IS a contract rather than prose.
CONTRACT = re.compile(r'ToString\("|"@odata|"\$|"O"|"c"|"D"|ContentType|MediaType|"true"|"false"|'
                      r'Header|"W/|charset|application/')


def bucket(fname, mutator, code):
    unmapped = fname not in CONCERN
    concern, why = CONCERN.get(fname, ('U', 'unclassified -- no concern recorded'))
    line = code.strip()

    if mutator == 'String mutation':
        if CONTRACT.search(line):
            return concern, 'contract string'
        if PROSE.search(line):
            return 'C', 'message/log prose'
        # An unmapped file must never be DECIDED by this branch. "Not evidently a contract"
        # is a judgment about the string, and nobody has made one about this file yet.
        if unmapped:
            return 'U', 'unclassified -- string in a file with no concern recorded'
        return 'C', 'string, not evidently a contract'

    # Prose is prose whatever the mutator. Gating this on String mutation left Statement and
    # Block mutations over `errors.Add(...)` / `throw new ...` inheriting the file's concern,
    # which put startup-refusal bookkeeping in tier A.
    if PROSE.search(line):
        return 'C', 'message/log prose (non-string mutator)'

    return concern, mutator


r = json.load(io.open(REPORT, encoding='utf-8'))
rows = []
for path, f in r['files'].items():
    fname = os.path.basename(path.replace('\\', '/'))
    src = f['source'].split('\n')
    for m in f['mutants']:
        if m['status'] not in ('Survived', 'NoCoverage'):
            continue
        ln = m['location']['start']['line']
        code = src[ln - 1] if ln - 1 < len(src) else ''
        tier, why = bucket(fname, m.get('mutatorName', '?'), code)
        rows.append({'file': fname, 'line': ln, 'mutator': m.get('mutatorName', '?'),
                     'status': m['status'], 'tier': tier, 'why': why, 'code': code.strip()[:100]})

tiers = collections.Counter(x['tier'] for x in rows)
surv = sum(1 for x in rows if x['status'] == 'Survived')
# Survived and NoCoverage are both "nothing objected", but they are NOT the same finding:
# Survived means a test ran and passed anyway; NoCoverage means no test reached the line.
# Keep them separate in every headline figure.
print('SURVIVED: %d   UNCOVERED: %d   TOTAL: %d' % (surv, len(rows) - surv, len(rows)))
print('tiers: %s' % ' '.join(
    '%s=%d(%ds/%du)' % (t, tiers[t],
                        sum(1 for x in rows if x['tier'] == t and x['status'] == 'Survived'),
                        sum(1 for x in rows if x['tier'] == t and x['status'] != 'Survived'))
    for t in ('A', 'B', 'U', 'C') if tiers[t]))
print()

for tier, label in (('A', 'DECIDE ABOUT -- a wrong answer here is SILENT and harmful'),
                    ('B', 'REVIEW -- wrong but inspectable'),
                    ('U', 'UNCLASSIFIED -- no concern recorded; decide before trusting the bucket'),
                    ('C', 'DROP -- message prose, log-only, or not a contract')):
    sel = [x for x in rows if x['tier'] == tier]
    byfile = collections.Counter(x['file'] for x in sel)
    print('=== TIER %s  (%d)  %s' % (tier, len(sel), label))
    for fn, n in byfile.most_common():
        note = CONCERN.get(fn, ('', ''))[1]
        print('    %-38s %4d   %s' % (fn, n, note))
    print()

if OUT:
    with io.open(OUT, 'w', encoding='utf-8', newline='') as fh:
        fh.write('# Mutation-survivor triage\n\n')
        fh.write('Ranked by failure-mode silence. %d survivors + uncovered mutants.\n\n' % len(rows))
        for tier, label in (('A', 'Decide about'), ('B', 'Review'), ('U', 'Unclassified'), ('C', 'Dropped')):
            sel = [x for x in rows if x['tier'] == tier]
            fh.write('## Tier %s -- %s (%d)\n\n' % (tier, label, len(sel)))
            if tier == 'C':
                fh.write('Message prose, log-only statements, and strings that are not contracts. '
                         'Killing these means pinning implementation text; decided against, once.\n\n')
                byfile = collections.Counter(x['file'] for x in sel)
                for fn, n in byfile.most_common():
                    fh.write('- `%s` &mdash; %d\n' % (fn, n))
                fh.write('\n')
                continue
            for fn in sorted({x['file'] for x in sel}):
                items = [x for x in sel if x['file'] == fn]
                fh.write('### `%s` (%d)\n\n' % (fn, len(items)))
                note = CONCERN.get(fn, ('', ''))[1]
                if note:
                    fh.write('%s\n\n' % note)
                fh.write('| Line | Mutation | Source |\n|---|---|---|\n')
                for x in sorted(items, key=lambda y: y['line'])[:40]:
                    fh.write('| %d | %s%s | `%s` |\n' % (
                        x['line'], x['mutator'],
                        ' *(uncovered)*' if x['status'] == 'NoCoverage' else '',
                        x['code'].replace('|', '\\|')))
                if len(items) > 40:
                    fh.write('| &hellip; | %d more | |\n' % (len(items) - 40))
                fh.write('\n')
    print('wrote', OUT)
