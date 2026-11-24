# Moteur Euphoria (NaturalMotion) dans Brutality

Le moteur « Euphoria » est un **portage de Delirium**, le projet open-source issu de la rétro-ingénierie
d'Euphoria (le moteur des GTA). Seules les lois utiles à un ragdoll de SPT sont traduites.

Depuis la version 0.2.0, c'est le **seul moteur d'animation** du plugin : l'ancien mode « Brutality »
(chorégraphies de chute procédurales) a été retiré. L'agonie, le posturing cérébral, la respiration
et les réflexes de Brutality restent actifs et se mélangent aux comportements Delirium dans le même
tampon moteur.

## Sources
- **[PVCPC/delirium-physics](https://github.com/PVCPC/delirium-physics)** (Rust, GPL-3.0) : réimplémentation
  d'Euphoria à partir du binaire de GTA IV. Le portage suit son architecture (`nmrs` = squelette et effecteurs,
  `nmdbm` = équilibre dynamique, `nmbb` = balancier du haut du corps, `nmshot` = comportement `shot`, `nmcf`,
  `nmrds`, `nmbw`, `nmbf`, `nmiog`) et ses lois numériques (cône de consigne, `blend_to_zero`, `set_stiffness`,
  `oppose_gravity`, point de capture, contrebalancement des bras, enveloppes de geste).
- **behaviours.xml de GTA V** : les valeurs numériques de Delirium sont lues dans le binaire de GTA IV (macro
  `kv!`) et ne sont pas redistribuables. Le portage conserve donc les **lois** et reprend les valeurs publiées par
  NaturalMotion (`NmParams.cs`, via
  [alexguirre/GTA-V-Euphoria-Behaviours-Parser](https://github.com/alexguirre/GTA-V-Euphoria-Behaviours-Parser)).
  Les endroits où Delirium choisit une constante propre sont signalés par un commentaire « Delirium : ».
- Contraction idiomusculaire post-mortem (phénomène de Zsako) : Madea (éd.), *Estimation of the Time Since Death*,
  3e éd., CRC 2016, chapitre sur les réactions supravitales. (Programme Brutality, hors Delirium.)

## Architecture du portage
| Fichier | Contenu |
|---|---|
| `Euphoria/Delirium/DlCharacter.cs` | `DlCharacter` : effecteurs par articulation et degré de liberté (angle désiré absolu, raideur ω, amortissement ζ, `muscleStiffness`, `oppose_gravity`), masques de message (`ub`, `ua`, `uc`, `uw`, `us`, `un`, `ll`, `lr`, `lb`), `blend_to_zero`, cône de consigne ; `DlBody` (capteurs, poussées et couples par segment) ; `DlPhys` (oppose-gravity en couple réel, raycast, bruit, enveloppe). |
| `Euphoria/Delirium/DlBalance.cs` | `bodyBalance` / `staggerFall` / `shotFallToKnees` : loi du haut du corps, contrebalancement des bras, gestionnaire d'équilibre (point de capture, pas, bassin, IK des jambes), forces de soutien et de redressement. |
| `Euphoria/Delirium/DlShot.cs` | machine du comportement `shot` : relâchement, réaction, douleur du rachis, main sur la plaie, bras projeté, jambe qui se dérobe, regard, vrille de choc. |
| `Euphoria/Delirium/DlBehaviours.cs` | `catchFall`, `rollDownStairs`, `bodyRollUp`, `armsWindmill`, `pedalLegs`, `bodyWrithe`, `bodyFoetal`, `injuredOnGround`. |
| `Euphoria/Delirium/DlGrab.cs` | `grab` + `GrabHelper` (sondes décor 8 directions, prise physique à force de rupture, pronation paume, libération après arrachement) et `upperBodyFlinch` (`nmfl`). |
| `Euphoria/NmPerformance.cs` | orchestrateur : plan de mort, enchaînement des phases (`Balance → Shot → Flinch/Chute → Grab → Sol`), impulsions balistiques, snap, amortissement de rotation, `setCharacterHealth` (vivacité résiduelle), journal. |

## Unités des effecteurs
- Delirium : `set_stiffness(s, d)` stocke `strength = s²` et `damping = 2·s·d` ; son moteur en déduit
  `stiffness = √strength` (pulsation propre ω) et `damping ratio = damping / (2·stiffness)`. Brutality écrit
  exactement ces deux grandeurs dans `MotorCommandBuffer.AddEffector`, donc **ω = s et ζ = d sans conversion**.
- `muscleStiffness` multiplie la raideur passive (1,01 − looseness).
- `oppose_gravity` est une **fraction** (0..1) du couple de gravité du segment, appliquée en couple réel (enfant +
  réaction sur le parent). Elle ne sert qu'aux membres (bras, jambes) : l'appliquer au rachis annulerait la gravité du
  tronc et le corps ne tomberait plus. Le poids du corps est porté par les jambes et leurs appuis, jamais par une
  poussée verticale du balancier.
- Les comportements n'agissent jamais directement sur un rigidbody : ils écrivent les effecteurs, puis
  `DlCharacter.Flush` les transmet une fois par pas aux drives PhysX. Les programmes Brutality (posturing,
  verrouillages, agonie, respiration) écrivent dans le même tampon et se mélangent.

## Correspondance des messages
| Message Euphoria (Delirium) | Portage |
|---|---|
| `bodyBalance`, `staggerFall`, `shotFallToKnees` | `DlBalance` (+ `stayUpright` : soutien du poids et « dernier carré » sous une rafale) |
| `shot` (`nmshot`) | `DlShot` |
| `configureBullets`, `applyBulletImpulse` | `NmPerformance.OnBullet` + `NmImpulseQueue` (impulsion triangulaire, part corps rigide, amortissement par rafale) |
| `shotSnap`, `shotShockSpin`, `setCharacterDamping` | `NmPerformance.Forces`, `DlShot.Forces` |
| `catchFall`, `rollDownStairs`, `bodyRollUp`, `armsWindmill`, `pedalLegs` | `DlBehaviours` |
| `grab`, `upperBodyFlinch` | `DlGrab` (`DlGrab` + `GrabHelper` + `DlFlinch`) |
| `injuredOnGround`, `bodyWrithe`, `bodyFoetal` | `DlBehaviours`, en cohabitation avec l'agonie Brutality |
| `setCharacterHealth` | `NmPerformance.U(t)` : la vivacité s'éteint avec la vie (le corps devient mou) |

## Preuves formelles et fidélité Delirium (0.3.0)
La suite `tests/Brutality.DeliriumTests` compile et exécute directement les vraies sources C# du plugin dans une simulation 3D hors-jeu à 60 Hz :
- **Ordre d'exécution (`nm.rs`)** : `Balance` s'exécute avant `Shot` afin de ne pas écraser la main sur la plaie ni les genoux en dérobement.
- **Lois effecteurs (`nmrs`)** : `set_stiffness(s, d)` (`strength = s²`, `damping = 2·s·d`, `ω = √strength`, `ζ = damping / (2·ω)`), `set_relaxation` à `ζ` invariant, cône `1 + 25 / (strength + 256)`, masques et `lock`/`unlock`, émission du DOF 2 de pronation sur les coudes.
- **Physique réelle (`nmdbm`, `nmshot`, `nmgr`)** : `oppose_gravity` borné et projeté sur l'axe des charnières (action + réaction exactes), `Shot::wrist` / `ForearmPronationForNormal`, `Shot::pull` action/réaction main ↔ plaie, `shockSpin` en N·m proportionnel au bras de levier, `Grab` sur murs/rebords sans faux positif sur sol plat, et dérobements unilatéraux (1 jambe), synchrones (2 jambes) et décalés (2 temps).

Le tableau complet des **77 assertions** (31 Delirium + 46 Cinématique/ISB/BepInEx), les empreintes SHA-256 vérifiables et les scénarios à reproduire dans `demo/banc-essai.html` sont regroupés dans [`docs/VERIFICATION.md`](VERIFICATION.md).

## Ce qui n'est pas porté, et pourquoi
- **SDK, playroom, lecteurs de fichiers** (XML/messages) : sans objet hors de l'outil de Delirium.
- **`getup`, `ledge` (escalade), `walk`/`run`, portails, skate** : supposent un agent vivant qui se relève ou se
  déplace ; un ragdoll de mort SPT ne les utilise jamais (`grab` est en revanche porté pour s'agripper aux murs, caisses et rebords pendant la chute).
- **Clavicules et poignets** : absents du ragdoll EFT (mains fusionnées avec l'avant-bras). Le masque `uc` vise les
  épaules, `uw` est vide.
- **`balancerCollisionsReaction`** (glissade contre un mur) : non porté ; le contact avec le décor reste géré par
  PhysX.
- **Articulations de cheville et de pied** : le pied n'est pas simulé ; l'inclinaison du bassin que Delirium
  transmet à la cheville va au rachis, pour ne pas combattre la cinématique inverse des jambes.

## Choix du plan et roulades
Delirium ne décide pas lui-même qu'un corps roule : c'est la tâche « shot » du jeu qui choisit le message
(`rollDownStairs`, `staggerFall`, …). Le portage reprend donc la loi de la roulade, mais la **sélection** du plan est à
nous :

- élan ≥ 2,4 m/s au moment de la mort (course, choc en mouvement) → roulade d'emblée ; ≥ 1,8 m/s → effondrement ;
- sinon tirage pondéré entre treize plans : titube, chute à genoux, glissade sur les genoux, quatre pattes,
  effondrement, effondrement assis, effondrement latéral, plongeon, chute en planche, moulinets, pivot, vrille et
  roulade (poids 0,65 à 0,85 × variété pour les plans qui demandent de l'élan ; × 1,4 à 2 selon le tir, la posture
  accroupie, la jambe touchée et le mouvement) ;
- la bascule de perte d'équilibre est orientée : un azimut est tiré dans le repère du bassin (0 = avant, π/2 = droite,
  π = arrière), décalé par l'axe du tir et par le plan. Elle est appliquée comme un couple de corps entier sur 0,45 s,
  et non plus comme une unique bascule en avant ; c'est ce qui fait tomber le corps en avant, à la renverse, sur le
  flanc ou en diagonale selon la mort ;
- un **directeur de variété** retient les cinq derniers plans et divise leur poids jusqu'à 0,12 : un même plan ne
  revient pas avant plusieurs corps. Chaque mort reçoit en outre une signature (force de bascule, cadence des gestes,
  asymétrie) ;
- même le relâchement (mort encéphalique, souffle, section médullaire) est orienté : le corps s'affaisse dans l'axe de
  la poussée puis bascule avec une force tirée à chaque mort ;
- une roulade de plan garde un court instant d'équilibre (0,12–0,35 s) puis bascule en avant : sans cette bascule, le
  corps s'accroupit sur place et la rotation ne se voit pas ;
- pendant la chute, la roulade remplace les rattrapages dès 1,2 m/s d'élan latéral ;
- au sol, elle n'est plus coupée à l'instant du contact : elle continue (ou démarre) si le corps a encore de l'élan
  (> 1,4 m/s ou > 3 rad/s de rotation du thorax), puis s'éteint d'elle-même après 4 s en chute / 2,4 s au sol, ou dès
  que la translation retombe sous 0,45 m/s après 1,2 s ;
- au sol encore, un retournement peut être tiré (rouler sur le flanc ou se retourner) : c'est un couple de corps entier,
  pas une pose, donc l'agonie continue par-dessus ;
- la chute en planche (posturing) force une raideur de 15 et un rachis cambré pendant 1,5 s : le corps tombe à la
  renverse, raide, au lieu de s'effondrer.

## Gestes et variété des agonies
Les gestes involontaires ne sont pas seulement pondérés par le mécanisme : chaque mort tire une **palette** (quatre à
six gestes privilégiés, trois à cinq atténués), une cadence, une asymétrie et une amplitude de contorsions. La mémoire
des deux morts précédentes réduit en plus le poids des gestes déjà vus (`Core/VarietyDirector.cs`). Les gestes
transversaux ajoutés (pontage, reptation sur les coudes, tête qui frappe le sol, jambes croisées, bras au-dessus de la
tête, mains sur la poitrine, torsion axiale, talon qui glisse, épaules haussées, nuque cambrée, frisson) sont écartés
d'eux-mêmes par les capacités articulaires : une paraplégie ne fait pas de pontage, un bras détruit ne presse pas sa
poitrine.

Les moulinets ne démarrent plus en concurrence avec une roulade : les deux écrivaient la même posture.

## Son de chute
EFT ne joue le son de chute du cadavre (`Corpse.PlayCorpseDropSound`, banque `ProneDropSoundBank` selon la surface)
qu'à la **création du ragdoll** — dans `Corpse.TryToCreateRagdoll` —, donc au moment de la mort et pas au contact avec le
sol. Brutality le rejoue une seule fois quand le corps arrive réellement au sol (phase « au sol » d'Euphoria, ou baisse
de 35 cm du bassin et vitesse retombée sinon), et seulement si le corps a vraiment chu (vitesse verticale acquise
≥ 0,8 m/s) ; une mort allongée ne déclenche rien.

## Cadavres simulés en permanence
Après la phase active, le corps n'est plus figé. PhysX l'endort au repos, ce qui ne coûte presque rien. Une balle
(délégué de tir de `BallisticsCalculator`) le réveille : impulsion étalée, rebond contre le sol et contraction
idiomusculaire locale, puis nouvelle réaction `shot` si le mourant est encore conscient. Le rappel
`OnRigidbodyStopped` du jeu est appelé une seule fois. Au-delà du plafond de cadavres simulés, les plus anciens sont
figés comme en vanilla. Les corps observés d'un client Fika gardent le comportement vanilla.
