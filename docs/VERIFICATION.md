# Brutality 0.3.0 — Fiche de preuves formelles

> **Document généré automatiquement** par l'exécution des suites de tests hors jeu (`Brutality.DeliriumTests` et `Brutality.KinematicsTests`) directement sur les vraies sources C# du plugin. Aucune ligne de résultats ci-dessous n'est saisie à la main.

## Métadonnées & traçabilité

| Champ | Valeur |
|---|---|
| Version du plugin | `0.3.0` |
| Date d'exécution | `2026-11-24 13:39 UTC` |
| Runtime & OS | `.NET 10.0.14` / `Unix 7.3.5.2` |
| Empreinte SHA-256 des 14 sources C# testées | `601237ccb2fc8ce725cacedcda6be58028da25ef0d13336fcb76fe753e87414b` |
| Empreinte SHA-256 de `Brutality.dll` (Release) | `d4e6bcb3592881ee67d8808f8eb6c6bf808b4bafc199efb89f65576e23f19f48` (235,008 octets) |
| Reproduction locale | `dotnet run --project tests/Brutality.DeliriumTests -- --report docs/VERIFICATION.md` puis `dotnet run --project tests/Brutality.KinematicsTests -- --append-report docs/VERIFICATION.md` |

## Synthèse globale

| Suite | Périmètre | Résultat | Statut |
|---|---|---:|---|
| `tests/Brutality.DeliriumTests` | Lois Delirium (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nmcf`, `nmgr`, `nmrds`, `nmfl`), simulation 60 Hz & diversité 60 morts | **31 / 31** | `PASS` |
| `tests/Brutality.KinematicsTests` | Anatomie ISB, IK, solveur de pose, enveloppe thoracique, clés BepInEx | **46 / 46** | `PASS` |
| **Total vérifié** | **Preuves formelles complètes hors jeu** | **77 / 77** | `PASS` |

## Niveau 1 — Fidélité mathématique loi par loi (Delirium Rust vs Brutality C#)

| # | Référence Delirium | Source Brutality | Invariant vérifié | Mesure observée | Statut |
|---:|---|---|---|---|---|
| 1 | `nmrs.rs (set_stiffness)` | `DlCharacter.cs (SetStiffness)` | nmrs::set_stiffness : strength = s², damping = 2·s·d | `(strength=121,0, damping=16,5)` | `PASS` |
| 2 | `nmrs.rs (muscles)` | `DlCharacter.cs (Flush)` | nmrs::muscles → Flush : ω = √strength et ζ = damping/(2·ω) sans conversion | `(ω=11,00, ζ=0,75, ms=0,90)` | `PASS` |
| 3 | `nmrs.rs (set_relaxation)` | `DlCharacter.cs (SetRelaxation)` | nmrs::set_relaxation(0.4) : ω = 0.4·base, ζ invariant = baseDamping | `(ω=4,80, ζ=1,00)` | `PASS` |
| 4 | `nmrs.rs:545 (targets)` | `DlCharacter.cs (DlCfg.ConeRadius)` | nmrs::targets : ConeRadius(strength) = 1 + 25/(strength+256) décroissant avec strength | `(kWeak=1,096, kStiff=1,052)` | `PASS` |
| 5 | `nmrs.rs (set_relative, targets)` | `DlCharacter.cs (SetRelative, Targets)` | nmrs::set_relative + targets : muscle faible autorise débordement cône > muscle raide | `(weak=191,3°, stiff=186,1°, hi=180,0°)` | `PASS` |
| 6 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("fb") = 10 articulations | `(10 articulations)` | `PASS` |
| 7 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("ub") = 6 (rachis+cou+bras) | `(6 articulations)` | `PASS` |
| 8 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("ut") = 5 (tronc+bras, SANS cou) | `(5, Neck absent=True)` | `PASS` |
| 9 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("uk") = 2 (rachis+cou) | `(2 articulations)` | `PASS` |
| 10 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("uw") = 0 (poignet absent sur EFT) | `(0 articulations)` | `PASS` |
| 11 | `nmrs.rs (mask)` | `DlCharacter.cs (Mask)` | nmrs::mask("lb") = 4, "ll" = 2, "lr" = 2 | `(lb=4, ll=2, lr=2)` | `PASS` |
| 12 | `nmrs.rs (lock/unlock)` | `DlCharacter.cs (Lock/Unlock)` | nmrs::lock/unlock("lb") : préserve haut du corps et conserve jambes | `(shR=1,23 rad, kneeL=1,11 rad)` | `PASS` |
| 13 | `nmrs.rs (muscles) + nmshot` | `DlCharacter.cs (Flush DOF 2)` | Flush pronation coude (DOF 2) : émis sur coudes G/D, ignoré sur genoux | `(D=-48,7°, G=48,7°, genou=False)` | `PASS` |
| 14 | `nmshot (wrist)` | `DlCharacter.cs (DlPhys.ForearmPronationForNormal)` | Shot::wrist / ForearmPronationForNormal : paumes G/D tournées face au sol | `(pR=-75,0°, dotR=0,97 ; pL=75,0°, dotL=0,97)` | `PASS` |
| 15 | `nmdbm.rs (oppose_gravity)` | `DlCharacter.cs (DlPhys.OpposeGravity)` | nmdbm::oppose_gravity : action+réaction=0, couple borné et charnière projetée sur l'axe | `(\|Στ\|=0,0000, τSh=8,4 N·m, τEl=2,3 N·m, align=1,000)` | `PASS` |
| 16 | `nmbb.rs (BodyBalance)` | `DlBalance.cs (Tick)` | nmbb::BodyBalance : épaule=11, coude=6.6, Busy préservé, BlendToZero ne fige plus sur le fusil | `(ωSh=11,0, ωEl=6,6, busyKept=True, shDes=50,3°)` | `PASS` |
| 17 | `nm.rs (ordre Balance → Shot)` | `NmPerformance.cs (Tick)` | nm.rs ordre Balance → Shot : Shot survit à Balance (Reach actif, genou collapsé non écrasé) | `(reached=True, ωKneeL=3,7, ωKneeR=6,0, phase=Fall)` | `PASS` |

## Niveau 2 — Preuves comportementales en simulation physique 60 Hz

| # | Référence Delirium | Source Brutality | Invariant vérifié | Mesure observée | Statut |
|---:|---|---|---|---|---|
| 18 | `nmshot (reach, pull, wrist)` | `DlShot.cs (Reach, Forces)` | nmshot::reach + pull + wrist : main rapprochée de la plaie, pull action/réaction exact, pronation active | `(d0=0,57m→min=0,42m, pull=True, ΣF=0:True, pron=22,7°)` | `PASS` |
| 19 | `nmshot (shock_spin)` | `DlShot.cs (OnBullet, Forces)` | nmshot::shock_spin : couple en N·m (20..90 N·m, pas Deg2Rad) proportionnel au bras de levier | `(spin=54,8 N·m, τThorax=54,8 N·m)` | `PASS` |
| 20 | `nmshot (LOOSE_L)` | `DlShot.cs (OnBullet, Tick)` | nmshot::LOOSE_L : bras gauche touché devient flasque, bras droit saisit le bras blessé | `(ωElL=2,2, ωElR=12,0, reachR=True)` | `PASS` |
| 21 | `nmshot (GiveWay)` | `DlShot.cs, DlBalance.cs` | nmshot::GiveWay (balle dans le dos) : raideur genou chute (<4), genoux fléchissent, bassin descend | `(minωKnee=1,8, kneeFlex=106,8°, drop=0,42m)` | `PASS` |
| 22 | `nmshot + nmdbm` | `DlBalance.cs (LegBuckle, Forces)` | Dérobement jambes : 1 jambe (unilatéral), 2 jambes (synchrone) ET 2 jambes décalées (2 temps) prouvés | `(uni=True, sync=True, décaléMi=True, deuxTard=True)` | `PASS` |
| 23 | `nmcf (CatchFall)` | `DlBehaviours.cs (DlCatchFall)` | nmcf::CatchFall : sonde vers le SOL (y<0), forward>0, touche le sol, cou en rad, pronation paumes | `(down=True, fwd=True, hit=True, neckRad=True, pron=True)` | `PASS` |
| 24 | `nmgr (Grab, GrabHelper)` | `DlGrab.cs (DlGrab)` | nmgr::Grab + GrabHelper : détecte mur & caisse, 0 faux positif sol plat, prise physique + force + rupture + libération Busy | `(act=True, grip=True, force=True, snap=True, rel=True[b0=False,b1=False,gAct=False], openFP=False, crate=True)` | `PASS` |
| 25 | `nmrds + nmru` | `DlBehaviours.cs (DlRollDownStairs, DlRollUp)` | nmrds::RollDownStairs + nmru::RollUp : roulade active, rotation réelle, Lumbar sans oppose_gravity | `(plan=RollDownStairs, peakW=2,5 rad/s, lumbarOg0=True)` | `PASS` |
| 26 | `nmaw (ArmsWindmill)` | `DlBehaviours.cs (DlWindmill)` | nmaw::Windmill : bras déphasés (≠ robot synchrone) et arrêt individuel au contact | `(phase=2,98 rad, Δsh=81,8°, leftStopped=True)` | `PASS` |
| 27 | `nmbw (BodyWrithe)` | `DlBehaviours.cs (DlWrithe)` | nmbw::Writhe : jambes dissymétriques G/D + vrai couple rollOver en Forces() | `(Δhip=5,5°, peakRollTq=12,0 N·m)` | `PASS` |
| 28 | `nmbf (BodyFoetal)` | `DlBehaviours.cs (DlFoetal)` | nmbf::Foetal : asymétrie de raideur par articulation + pronation croisée sur la poitrine | `(sL=72,9, sR=96,8, pronL=42,8°, pronR=-19,2°)` | `PASS` |
| 29 | `nmfl (UpperBodyFlinch)` | `DlGrab.cs (DlFlinch)` | nmfl::UpperBodyFlinch : réflexe protecteur sur rafale frontale + pronation avant-bras | `(flinch=True, peakPron=31,0°)` | `PASS` |

## Niveau 3 — Audit statistique de diversité cinématique (60 morts tactiques, graines 1000..1059)

| # | Référence Delirium | Source Brutality | Invariant vérifié | Mesure observée | Statut |
|---:|---|---|---|---|---|
| 30 | `VarietyDirector + NmPlan` | `NmPerformance.cs, VarietyDirector.cs` | Diversité 60 morts : ≥ 8 plans distincts, aucun plan > 35 %, 3 secteurs couverts | `(12 plans, max=9/60, Av/Cot/Ar=26/14/20)` | `PASS` |
| 31 | `Suite complète Delirium` | `DlBalance, DlShot, DlGrab, DlBehaviours` | Diversité 60 morts : agrippements décor + plaies + pronations + roulades + dérobements 1J & 2J tous prouvés | `(10 grabs, 30 plaies, 54 pronations, 9 roulades, 1J=12, 2J=30, NaN=0)` | `PASS` |

### Métriques observées sur 60 morts (12 scénarios × 5 itérations)

| Indicateur | Seuil formel | Mesure observée | Statut |
|---|---|---|---|
| Plans Euphoria distincts | `>= 8` | `12` | `PASS` |
| Part maximale d'un même plan | `<= 21 / 60 (35 %)` | `9 / 60 (15,0 %)` | `PASS` |
| Secteurs d'azimut (Avant / Côté / Arrière) | `>= 5` par secteur | `26 / 14 / 20` | `PASS` |
| Agrippements au décor (`grab`) | `>= 6` | `10 / 60` | `PASS` |
| Mains portées sur la plaie (`Shot::reach`) | `>= 25` | `30 / 60` | `PASS` |
| Pronations avant-bras (`> 15°`) | `>= 35` | `54 / 60` | `PASS` |
| Roulades (`rollDownStairs` / `rollUp`) | `>= 5` | `9 / 60` | `PASS` |
| Dérobement 1 jambe (unilatéral) | `>= 8` | `12 / 60` | `PASS` |
| Dérobement 2 jambes (synchrone / décalé) | `>= 8` | `30 / 60` | `PASS` |
| Moulinets des bras (`armsWindmill`) | informatif | `16 / 60` | `INFO` |
| Réflexes de garde (`upperBodyFlinch`) | informatif | `12 / 60` | `INFO` |
| Erreurs numériques (`NaN`) | `0` | `0` | `PASS` |

### Répartition observée par plan Euphoria (`NmPlanKind`)

| Plan | Effectif (sur 60) | Part |
|---|---:|---:|
| `RollDownStairs` | 9 | 15.0 % |
| `FallToKnees` | 8 | 13.3 % |
| `SideCrumple` | 7 | 11.7 % |
| `Stagger` | 7 | 11.7 % |
| `HandsKnees` | 6 | 10.0 % |
| `Windmill` | 6 | 10.0 % |
| `Relax` | 5 | 8.3 % |
| `TwistDown` | 5 | 8.3 % |
| `Collapse` | 2 | 3.3 % |
| `Pivot` | 2 | 3.3 % |
| `SitBack` | 2 | 3.3 % |
| `KneeSlide` | 1 | 1.7 % |

## Niveau 4 — Cinématique, anatomie ISB et configuration BepInEx

Tests exécutés sur `Core/BMath.cs`, `Core/JointKinematics.cs`, `Anatomy/AnatomyData.cs` et `Settings.cs` sans aucune dépendance EFT/SPT.

| # | Domaine | Vérification | Mesure observée | Statut |
|---:|---|---|---|---|
| 1 | Genou | genou droit tendu = 0° | `conforme` | `PASS` |
| 2 | Genou | genou : jambe vers l'arrière = flexion +60° | `(60,0°)` | `PASS` |
| 3 | Genou | genou : hyperextension = négatif | `conforme` | `PASS` |
| 4 | Genou | genou fléchi à la mort : 60° relu | `conforme` | `PASS` |
| 5 | Genou | genou fléchi : +10° vers l'arrière = 70° | `conforme` | `PASS` |
| 6 | Coude | coude : avant-bras vers l'avant = flexion +90° | `(90,0°)` | `PASS` |
| 7 | Coude | coude : prono-supination lue sur la torsion | `conforme` | `PASS` |
| 8 | Hanche | hanche neutre = 0/0/0 | `conforme` | `PASS` |
| 9 | Hanche | hanche : cuisse vers l'avant = flexion +45° | `conforme` | `PASS` |
| 10 | Hanche | hanche droite : écartement = abduction +30° | `(30,0°)` | `PASS` |
| 11 | Hanche | hanche gauche : écartement = abduction +30° | `(30,0°)` | `PASS` |
| 12 | Hanche | hanche : cuisse vers l'arrière = extension (négatif) | `conforme` | `PASS` |
| 13 | Épaule | épaule : bras levé devant à 170° | `conforme` | `PASS` |
| 14 | Épaule | épaule : 190° lu 190° (pas -170°) | `(190,0°)` | `PASS` |
| 15 | Épaule | épaule droite : abduction +60° | `conforme` | `PASS` |
| 16 | Épaule | épaule gauche : bras vers la ligne médiane = adduction (négatif) | `conforme` | `PASS` |
| 17 | Rotation interne & pronation | épaule droite : rotation interne lue ±30° | `(-30,0°)` | `PASS` |
| 18 | Rotation interne & pronation | rotation interne : signe = -côté (droite -, gauche +) | `(D -30,0°, G 30,0°)` | `PASS` |
| 19 | Rotation interne & pronation | hanche : rotation interne, même convention que l'épaule | `(D -30,0°, G 30,0°)` | `PASS` |
| 20 | Rotation interne & pronation | coude droit : pronation (paume vers l'intérieur puis l'arrière) = négatif | `(-40,0°)` | `PASS` |
| 21 | Rachis lombaire | rachis neutre = 0/0/0 | `conforme` | `PASS` |
| 22 | Rachis lombaire | rachis : penché en avant = flexion +30° | `conforme` | `PASS` |
| 23 | Rachis lombaire | rachis : inclinaison à droite = +20° | `conforme` | `PASS` |
| 24 | Rachis lombaire | rachis : torsion +25° lue +25° | `conforme` | `PASS` |
| 25 | Rachis lombaire | torsion positive = épaule gauche vers l'avant (roulade vers la droite) | `conforme` | `PASS` |
| 26 | Rachis lombaire | rachis : torsion à la mort relue (20°) | `conforme` | `PASS` |
| 27 | Cou | cou : torsion +40° = regard vers la droite | `conforme` | `PASS` |
| 28 | Cou | cou : menton vers la poitrine = flexion +30° | `conforme` | `PASS` |
| 29 | Gradients numériques | gradients numériques cohérents (dφ = g·ω dt) | `(180/180)` | `PASS` |
| 30 | Cinématique inverse | IK : angles d'épaule ↔ direction du bras | `(flex 2°, abd 27°, coude 99°)` | `PASS` |
| 31 | Cinématique inverse | IK : l'avant-bras atteint la cible | `conforme` | `PASS` |
| 32 | Cinématique inverse | IK : coude fléchi pour une cible proche | `conforme` | `PASS` |
| 33 | Cinématique inverse | IK jambe G : la jambe atteint la cheville cible | `(hanche 29°/2°, genou 19°)` | `PASS` |
| 34 | Cinématique inverse | IK jambe G : genou fléchi vers l'avant, cuisse fléchie | `conforme` | `PASS` |
| 35 | Cinématique inverse | IK jambe D : la jambe atteint la cheville cible | `(hanche 29°/2°, genou 19°)` | `PASS` |
| 36 | Cinématique inverse | IK jambe D : genou fléchi vers l'avant, cuisse fléchie | `conforme` | `PASS` |
| 37 | Cinématique inverse | IK jambe : appui vertical = jambe presque tendue | `(5,5°, 0,0°, 10,9°)` | `PASS` |
| 38 | Solveur de pose | solveur de pose : cibles anatomiques atteintes (< 0,5°) | `(240/240, pire 0,05°)` | `PASS` |
| 39 | Solveur de pose | solveur de pose : pose centrale de chaque articulation | `conforme` | `PASS` |
| 40 | Stabilité, axes & enveloppe thoracique | coude replié en travers du torse : flexion positive | `(114,9°)` | `PASS` |
| 41 | Stabilité, axes & enveloppe thoracique | JointAxes analytiques : sens flexion + orthogonalité + épaule 89,5° | `conforme` | `PASS` |
| 42 | Stabilité, axes & enveloppe thoracique | SolvePose : épaule à 200° revient par l'avant + quaternion normalisé | `(final 15,0°, \|q\|=1,00000)` | `PASS` |
| 43 | Stabilité, axes & enveloppe thoracique | StepCriticallyDamped : stable et sans dépassement de 60 Hz à 4 Hz | `conforme` | `PASS` |
| 44 | Stabilité, axes & enveloppe thoracique | épaule : adduction bloquée à 0° le long du thorax, ouverte devant (~-22°), resserrée près de la tête | `(0°:-0,0°, -30°:-0,0°, 90°:-22,0°, 175°:-8,3°)` | `PASS` |
| 45 | Stabilité, axes & enveloppe thoracique | enveloppe thoracique : détection, normales sortantes et projection extérieure | `(dSide=61cm, dFront=111cm)` | `PASS` |
| 46 | Configuration BepInEx | configuration : sections et clés valides pour BepInEx | `(37 noms)` | `PASS` |

## Vérifier de ses propres yeux dans le studio 3D (`demo/banc-essai.html`)

Le studio 3D embarqué dans le dépôt permet de reproduire visuellement chaque comportement ci-dessus dans un navigateur, avec la même logique que la version `0.3.0` :

1. **Agrippement au mur + rupture (`DlGrab`)** — Choisir le décor **`Mur proche à droite (grab)`**, posture **`Debout`**, comportement **`Auto`** (ou **`Effondrement latéral`**), puis cliquer sur le flanc gauche du mannequin pour le projeter vers la droite : le bras droit se tend vers le mur, l'avant-bras effectue une pronation paume vers la paroi, un point d'ancrage jaune apparaît au contact puis cède lorsque la charge dépasse le seuil.
2. **Agrippement à une caisse / rebord (`GrabHelper`)** — Choisir **`Caisse / rebord devant (grab)`**, posture **`Debout`**, et tirer dans le dos ou le thorax pour basculer vers l'avant : les sondes détectent l'arête supérieure de la caisse devant le corps et verrouillent une prise.
3. **Dérobement 1 ou 2 jambes & balle dans le dos (`GiveWay`)** — Choisir **`Pièce standard`**, tourner la caméra derrière le mannequin, et tirer dans le dos ou dans une cuisse : le bassin s'affaisse sans raidir les jambes, avec flexion visible d'un genou (tir jambe) ou des deux jambes (simultané ou décalé).
4. **Réflexe de garde (`upperBodyFlinch`) & « dernier carré »** — Choisir **`Pièce standard`**, posture **`Debout`**, et cliquer **`Rafale ×5`** (touche **`B`**) de face : les bras montent protéger le haut du corps avec pronation des avant-bras tandis que le tronc résiste brièvement avant de céder.
5. **Roulade d'élan (`rollDownStairs` / `bodyRollUp`) et comparaison vanilla** — Passer la posture sur **`En course (3,5 m/s)`** ou **`Sprint (5,5 m/s)`**, réduire **`Vitesse de simulation`** à **`0,25×`** et tirer dans le thorax pour observer la roulade et l'agonie au sol ; activer **`Pose visée par les muscles (fantôme)`** ou cocher **`Ragdoll inerte, sans Brutality (comparaison)`** puis cliquer **`Rejouer`** sur la même graine pour comparer directement avec le ragdoll inerte.

## Périmètre des preuves et transparence

| Ce qui est prouvé formellement hors jeu | Ce qui reste à valider en jeu (Unity / PhysX / SPT) |
|---|---|
| Toutes les lois mathématiques Delirium (`nmrs`, `nmdbm`, `nmbb`, `nmshot`, `nm.rs`) exécutées directement sur les vrais `.cs` du plugin. | Calibrage des ordres de grandeur sur les squelettes réels de Tarkov (`PhysxProbe` de sens des drives/butées en début de raid). |
| Boucle fermée physique 60 Hz (`Shot`, `Balance`, `Grab`, `CatchFall`, `RollDownStairs`/`RollUp`, `Windmill`, `Writhe`, `Foetal`, `Flinch`). | Interactions sur géométrie complexe des cartes SPT (escaliers, portes, mobilier) et masses complètes d'équipement porté. |
| Conventions ISB de tous les joints, IK bras/jambes, solveur de pose `SolvePose`, enveloppe thoracique, et validité de toutes les clés BepInEx. | Cohabitation en raid avec les mods tiers (`Smoothie`, `HollywoodFX`, `Fika`, `ReClassic`) et performance multi-corps. |
