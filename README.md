# Brutality 0.3.0 — ragdolls biomécaniques pour SPT

[![Preuves formelles Brutality](https://github.com/RedTheFoxx/SPT-BRUTALITY/actions/workflows/verify.yml/badge.svg)](https://github.com/RedTheFoxx/SPT-BRUTALITY/actions/workflows/verify.yml)

Plugin client BepInEx pour SPT 4.1.x qui remplace la dynamique de mort vanilla de Tarkov par une
simulation biomécanique fondée sur le corpus scientifique du projet
(`Corpus scientifique — biomécanique pour un ragdoll ultra réaliste.md`).

Le ragdoll vanilla est un pantin inerte que le jeu propulse avec une impulsion fixe de 150 N·s.
Brutality le transforme en **corps qui meurt**. Le système nerveux s'éteint selon la lésion, les
muscles perdent leur tonus, se contractent ou se figent, et les tissus résistent aux amplitudes
extrêmes. Tout s'arrête au plus tard **10 secondes** après la mort.

---

## Installation

```bash
dotnet build src/Brutality/Brutality.csproj -c Release -p:Install=true
```

Cette commande compile et copie `Brutality.dll` dans `SPT_4.1.6/BepInEx/plugins/Brutality/`.
Le chemin du jeu est défini dans `Directory.Build.props`. Pour une autre installation :
`-p:SptGame=/chemin/vers/SPT`.

Installation manuelle : copier `src/Brutality/bin/Release/Brutality.dll` dans `BepInEx/plugins/Brutality/`.

Il n'y a rien à installer côté serveur : le plugin est 100 % client. Avec Fika, il doit être
installé sur **l'hôte** (et idéalement sur les clients).

---

## Ce qui est simulé

### 1. Tissus passifs (le corps est mort, mais reste un corps)
- **Masses et inerties par segment** d'après de Leva (1996) : centres de masse et rayons de
  giration orientés sur l'axe longitudinal réel de chaque os. La masse corporelle varie d'un
  individu à l'autre (80 kg ± 10 % par défaut).
- **Équipement** : gilet, plaques, chargeurs et sac alourdissent le thorax et déplacent son
  centre de masse (le sac vers l'arrière). Casque et casque audio alourdissent la tête.
- **Amplitudes articulaires anatomiques** (NASA Source Book, Norkin & White, Panjabi & White).
  Elles sont exprimées dans des repères articulaires de type ISB (Wu 2002/2005) calculés sur le
  squelette au moment de la mort. Les limites vanilla sont en effet relatives à la pose de la
  mort, donc anatomiquement fausses.
- **Joints anatomiques PhysX** : les joints d'EFT sont remplacés à la mort par des
  `ConfigurableJoint` dont les butées sont les vraies amplitudes. La butée de flexion est exacte
  et asymétrique (un genou ne part pas en hyperextension). Son sens, comme celui des drives, est
  **mesuré en jeu** au début de chaque raid par une sonde PhysX invisible créée sous la carte. Elles
  sont « molles » (ressort amorti, raideur dérivée du couple passif
  exponentiel de Yoon & Mansour / Riener & Edrich) : la résistance monte au-delà de l'amplitude
  au lieu d'un arrêt sec.
- **Couplage bi-articulaire** : ischio-jambiers (hanche ↔ genou) et droit fémoral. Une jambe
  tendue ne peut pas monter au-delà d'environ 80°.
- **Viscosité articulaire** et **friction peau/vêtements** contre le sol (le corps ne glisse plus
  comme sur de la glace).

### 2. Muscles
- Actionneurs **couple-angle-vitesse** par degré de liberté (Anderson et al. 2007), avec relation
  force-vitesse de **Hill**, des couples maximaux distincts en flexion et en extension, et une
  **dynamique d'activation** de 15 ms à l'activation et 50 ms à la désactivation (Zajac, Thelen).
- Les muscles sont des **drives PhysX** (en accélération, donc indépendants des masses), intégrés
  implicitement par le solveur : aucune vibration numérique (Yeo et al. 2023). Les poses cibles
  anatomiques sont converties en rotation par un solveur de pose (moindres carrés amortis).
- **Collisions** : détection continue spéculative et vitesse angulaire bornée, pour que les membres
  ne traversent plus murs et sol.
- **Réponse localisée aux dégâts** : un membre détruit est flasque, un membre blessé est affaibli
  proportionnellement à sa vie restante. Le membre touché par le coup fatal lâche immédiatement.
- **Paralysies médullaires** : une balle dans la nuque (`NeckBack`) rend les quatre membres
  flasques. `SpineTop` paralyse les jambes et le tronc, `SpineDown` paralyse les jambes.

### 3. Système nerveux : chute et agonie
**Chutes Euphoria.** La chute n'est plus une chorégraphie figée : elle est jouée par les
comportements NaturalMotion du portage Delirium (`docs/EUPHORIA.md`). Le plan est choisi à la mort
d'après le mécanisme, la posture, la vitesse et la direction du tir :

| Plan | Ce qui se passe |
|---|---|
| Titube (`staggerFall`) | 2 à 5 pas réels sur le point de capture, genoux qui cèdent un peu plus à chaque appui, puis effondrement |
| Chute à genoux (`shotFallToKnees`) | le corps se dérobe sur les genoux, s'y maintient un instant, puis bascule |
| Glissade sur les genoux | genoux au sol et élan conservé : le corps glisse sur les genoux puis pique du nez |
| Quatre pattes | genoux puis mains, un temps d'appui, puis aplatissement |
| Effondrement | équilibre perdu très vite : le corps s'affaisse sur place |
| Effondrement assis | les hanches plient, le bassin tombe sous le poids, le buste part en arrière |
| Effondrement latéral | le corps part sur le côté, dans le plan frontal, du côté touché quand c'est possible |
| Plongeon | le buste pique en avant, les bras cherchent le sol |
| Chute en planche | rachis cambré et corps raide (posturing) : chute à la renverse, comme une planche |
| Moulinets (`armsWindmill`) | tir venu de face : les bras battent l'air, le buste part en arrière |
| Pivot | le corps tourne sur lui-même avant que l'équilibre ne cède |
| Vrille | le tronc se visse et le corps spirale vers le sol |
| Roulade (`rollDownStairs`) | élan au moment de la mort : bascule, roulé-boulé, puis agonie au sol |
| Relâchement | mort encéphalique, souffle, chute mortelle ou membre détruit : le tonus s'éteint, le corps tombe mou |

Chaque plan est en outre **orienté**. La bascule de perte d'équilibre est un couple de corps entier qui
part dans la direction du tir, à l'opposé, sur le côté ou en diagonale, selon le plan et un azimut tiré
à chaque mort : le corps tombe donc en avant, à la renverse, sur le flanc gauche ou droit, et jamais
toujours dans le même axe. Même le relâchement (balle à la tête, souffle, section médullaire) reçoit une
direction et une intensité de bascule propres : un corps sans tonus ne s'affaisse plus deux fois de la
même façon. Au sol, l'agonie peut faire rouler le corps sur le flanc ou le retourner.

**Mémoire des morts.** Un directeur de variété retient les plans des dernières morts et les pénalise
fortement : un même plan ne revient pas avant plusieurs corps, et chaque mort reçoit sa propre
signature (force de bascule, cadence des gestes, asymétrie du corps).

La réaction au tir est celle du comportement `shot` : relâchement, douleur du rachis, main sur la
plaie, bras projeté, jambe qui se dérobe, vrille de choc, et « dernier carré » quand une rafale
atteint un corps encore debout. Les muscles suivent les effecteurs Delirium ; la physique garde le
dernier mot sur les contacts au sol.

Chaque mort est classée selon son **mécanisme lésionnel** : zone exacte du collider, type de
dégât, calibre. Ce mécanisme fixe la chronologie de la mort :

| Mécanisme | Déclencheur | Chute | Agonie |
|---|---|---|---|
| Encéphalique | tête (crâne, yeux, oreilles, mâchoire) | blackout et chute instantanés : tonus éteint en 20-50 ms, sans posturing ni rigidité | myoclonies rares, faible |
| Médullaire cervical | nuque | tétraplégie flasque immédiate | tête seulement |
| Vasculaire cervical | gorge | sursaut, effondrement, mains à la gorge | intense, 5-8,5 s |
| Thoracique | cage thoracique haute | sursaut, genoux qui cèdent | myoclonies anoxiques, gasps, agrippe la poitrine |
| Abdominal | abdomen, bassin | défense, flexion du tronc | repli fœtal, roulades, mains sur la plaie |
| Membre | bras / jambe | chute du côté lésé | agrippe le membre, ruades de la jambe saine |
| Souffle | explosion | flaccidité immédiate, corps projeté | myoclonies |
| Syncope | hémorragie, épuisement, toxiques | affaissement progressif | myoclonies de syncope convulsive |
| Chute | dégâts de chute | inerte | quasi nulle |
| Brûlure | feu | sursaut | violente : roulades, repli, membres battants |

**Programmes moteurs involontaires.** Ils sont tirés au hasard et pondérés par le mécanisme,
l'orientation du corps au sol (sur le dos, le ventre ou le côté) et les membres encore capables
de bouger :

- tonus postural résiduel, qui s'éteint groupe par groupe ;
- verrouillages articulaires (gel musculaire) ;
- main vers la plaie, par cinématique inverse ;
- repli fœtal ;
- extension tonique (opisthotonos) ;
- salves de myoclonies ;
- pédalage ;
- ruade ;
- bras incontrôlé ;
- tentative de roulade ;
- rotation de tête ;
- gasps agoniques ;
- tête ballante ;
- bras qui se tendent brusquement (aussi à l'instant de l'impact) ;
- genoux qui se replient par saccades ;
- secousses globales en flexion ou en extension ;
- contorsions continues : chaque articulation dérive au hasard entre deux gestes francs ;
- **crises de décortication** (coudes et poignets fléchis, bras ramenés sur la poitrine, jambes raides), avec
  paroxysmes et clonus ;
- **rigidité de posture** continue entre les gestes, qui prolonge la posture de la chute (lésions cérébrales surtout) ;
- **signe de Lazare** : les bras se lèvent lentement et se croisent sur la poitrine, puis redescendent ;
- crise tonico-clonique : phase tonique puis secousses dont le rythme ralentit ;
- main tendue vers le ciel (ou devant soi), tremblante, qui retombe d'un coup ;
- tentative de ramper et tentative de se relever sur les bras (sur le ventre) ; avec une paraplégie, le corps se
  traîne sur les bras ;
- tentative de se redresser sur les coudes (sur le dos) ; tête qui se relève puis retombe ;
- genou qui remonte puis glisse ; talons qui tambourinent ; jambes en ciseaux ;
- frappe le sol du plat de la main ; bras qui protègent le visage ; mains au visage ; balancement d'un flanc à l'autre ;
- **pontage** (bassin soulevé en arc, appui sur les pieds et les épaules), **reptation sur les coudes**,
  **tête qui frappe le sol**, **jambes qui se croisent**, **bras tendus au-dessus de la tête**, **mains qui pressent
  la poitrine**, **torsion axiale continue**, **talon qui glisse vers la fesse**, **épaules haussées à chaque gaspe**,
  **nuque cambrée**, **frisson de tout le corps** ;
- **respiration laborieuse** visible (buste, tête, épaules), rapide puis de plus en plus lente, jusqu'à un
  **dernier soupir** : inspiration profonde, temps d'arrêt, puis la tête roule sur le côté.

Le même geste revient rarement deux fois de suite. Les gestes dépendent de l'orientation au sol (on ne rampe que sur
le ventre) et des membres encore capables de bouger.

À chaque mort, un tirage construit une **palette** propre : quatre à six gestes sont privilégiés, trois à cinq sont
atténués, et la cadence, l'asymétrie du corps et l'amplitude des contorsions varient avec elle. La mémoire des deux
morts précédentes réduit encore le poids des gestes déjà vus. Deux agonies ne jouent donc ni la même sélection, ni
le même ordre, ni la même cadence — et un bras détruit, une paraplégie ou un membre détaché écartent d'eux-mêmes les
gestes qu'ils rendent impossibles.

Un premier geste franc est garanti dès que le corps touche le sol, et jusqu'à trois gestes
peuvent se superposer.

**Impacts réels.** Une balle transmet sa quantité de mouvement p = m·v au point d'impact, avec
la masse et la vitesse lues dans le gabarit de la munition et une perte de vitesse selon la
distance. La part réellement transmise varie à chaque tir dans un intervalle propre à la classe de munition
(déformation, fragmentation, basculement, sortie du projectile), avec une légère déviation et un bras de levier
variable :

| Classe | Multiplicateur | Déviation |
|---|---|---|
| Pistolet | ×0,85 à 1,4 | 9° |
| Intermédiaire (5,45, 5,56…) | ×0,75 à 1,55 | 12° |
| Pleine puissance (7,62×51, 7,62×54R, .338…) | ×0,9 à 1,85 | 6° |
| Subsonique lourde (9×39, .300 BLK sub…) | ×1,05 à 1,75 | 5° |
| Gros calibre (12,7 mm…) | ×1,15 à 2,1 | 6° |
| Slug / chevrotine | ×1,1 à 2,1 / ×0,7 à 1,7 | 7° / 20° |
| Perforante (pénétration > 40) | intervalle réduit jusqu'à −28 % | ×0,7 |

Le tirage du coup fatal module aussi la force de la chute dirigée et l'intensité de la réaction à l'impact. Le
souffle des explosions varie de ×0,7 à ×1,4. Un souffle répartit son impulsion sur les segments selon leur masse et leur distance.
L'énergie du projectile module l'intensité des réflexes.

**Arrêt garanti en 10 s maximum.** L'agonie reste à pleine intensité pendant près de la moitié de
sa fenêtre, puis s'éteint au plus tard 1,5 s avant la fin. Une phase
de dissipation amène ensuite le corps au repos, puis le jeu fige les rigidbodies. Si le corps est
immobile plus tôt, l'arrêt est anticipé pour économiser le CPU.

---

### 3.1 Balle à la tête

Une balle dans la tête coupe le tonus en 20 à 50 ms : le corps part de sa hauteur, mou, sans se raidir. Aucun
posturing n'est joué (il suppose un tronc cérébral intact), aucune rigidité de fond ne s'installe, et la
respiration agonique est quasi absente. Les secousses médullaires libérées (clonies, spasmes, signe de Lazare)
subsistent plus tard, au sol.

### 3.2 Balle dans le dos

Un tir qui frappe le tronc par l'arrière ne fait plus basculer le corps d'un bloc sur des jambes tendues.
Le rachis ne se replie que de la moitié de la douleur habituelle, les jambes ne sont plus raidies, et le
quadriceps cède sous le poids du corps : le bassin descend, les genoux se ferment et la chute part d'un
affaissement des jambes. La roulade n'est plus déclenchée par une mort à l'arrêt ; elle reste possible si
la victime courait au moment du tir.

## Moteur Euphoria et cadavres simulés en permanence

La chute exécute un portage de [Delirium](https://github.com/PVCPC/delirium-physics), le projet
open-source issu de la rétro-ingénierie d'Euphoria (le moteur des GTA) : les lois de Delirium, avec les
valeurs par défaut du `behaviours.xml` de GTA V (les constantes de Delirium sont extraites du binaire
de GTA IV et ne sont pas publiables). Depuis la 0.2.0, c'est le **seul moteur d'animation** : les
chorégraphies procédurales de la 0.1.0 ont été retirées, l'agonie et le posturing cérébral de
Brutality restant actifs par-dessus.
Le mourant réagit au tir (relâchement, main sur la plaie avec traction `pull` et pronation de la paume, bras projeté, réflexe `upperBodyFlinch` sur rafale frontale), titube en posant de vrais pas, se dérobe d'une jambe ou des deux (simultanément ou en deux temps), tente de s'agripper aux murs, caisses et rebords proches (`grab` à force de rupture), fait des moulinets déphasés en partant en arrière, se rattrape ou roule (`rollDownStairs` + `bodyRollUp`), puis se tord au sol. Sous une rafale, il reste debout un instant (« dernier carré »). Les cadavres ne sont plus figés : ils restent simulés et bougent quand on leur tire dessus.
Détails, correspondance Delirium et preuves formelles : `docs/EUPHORIA.md` et `tests/Brutality.DeliriumTests`.

## Compatibilité avec les mods installés

| Mod | Statut | Action |
|---|---|---|
| **Smoothie** | Compatible. Son réglage « Corpses stop moving after (s) » (5 s chez toi) est neutralisé pour les corps gérés par Brutality. Son plafond de physique à 60 Hz est respecté : Brutality agit avant chaque pas `Physics.Simulate`. | **Rien à désactiver.** Facultatif : régler « Corpses stop moving after (s) » sur **10** pour aligner les corps non gérés (Brutality désactivé, corps en surnombre). |
| **HollywoodFX** | Compatible si ses ragdolls restent désactivés. C'est le cas dans ta config. Son option « Gore » applique à la mort une impulsion allant jusqu'à 200 N·s : Brutality la **neutralise** (option « Neutraliser les impulsions de mort des autres mods »). Le sang et les décalques sont conservés. | Garder « Enable Ragdoll Effects (RESTART) » et « Enable Cinematic Ragdolls (RESTART) » sur **false**. Si elles sont actives, HollywoodFX réanime les corps figés quand on leur tire dessus (un avertissement s'affiche dans le journal). |
| **Fika** | Compatible. L'hôte simule la mort ; les clients animent leur copie et la pose finale de l'hôte est appliquée par Fika. | Aucune. |
| **ReClassic** (inconscience) | Compatible. Un corps inconscient qui meurt est repris par Brutality. | Aucune. |
| **Rupture** (non installé) | Conflit : les deux mods pilotent les ragdolls. | Le désactiver s'il est installé un jour. |

---

## Configuration

Fichier `BepInEx/config/com.brutality.ragdoll.cfg`, modifiable en jeu avec F12
(ConfigurationManager).

| Section | Réglage | Défaut | Rôle |
|---|---|---|---|
| Général | Activer Brutality | true | Désactivé : comportement vanilla. |
| | Durée maximale du mouvement (s) | 10 | 3 à 10 s. |
| | Appliquer à votre propre corps | true | |
| | Appliquer aux corps observés (Fika client) | true | |
| | Corps simulés simultanément (max) | 12 | Au-delà : physique passive seule, sans agonie. |
| | Distance de simulation complète (m) | 150 | Au-delà : pas d'agonie. |
| Corps | Masse corporelle moyenne (kg) | 80 | |
| | Variation de masse | 0,10 | |
| | Inclure la masse du matériel porté | true | |
| | Friction des tissus au sol | 0,8 | |
| Comportement | Intensité des agonies | 1 | 0 = aucune agonie, 2 = double. |
| | Vigueur des spasmes | 1 | Netteté et force des gestes involontaires, amplitude des contorsions. 0,5 = plus sobre. |
| | Force des chutes dirigées | 1 | Intensité de la perte d'équilibre et de la bascule initiale des plans Euphoria. 0 = effondrement sous le poids seul. |
| | Intensité du posturing cérébral | 1 | Décérébration, décortication, signe de Lazare, chutes en planche. 0 = jamais. |
| | Variété des gestes | 1 | Fréquence des gestes élaborés et des chutes propres à la posture, écart entre les palettes de gestes d'une mort à l'autre. 0 = gestes de base et palettes neutres. |
| | Respiration agonique visible | true | Respiration laborieuse et dernier soupir. |
| | Intensité des réflexes | 1 | Sursaut, posturing, verrouillages. |
| | Force musculaire | 1 | |
| | Raideur passive des articulations | 1 | |
| Impacts | Multiplicateur de quantité de mouvement des balles | 1 | 1 = physique réelle. |
| | Multiplicateur du souffle des explosions | 1 | |
| | Neutraliser les impulsions de mort des autres mods | true | |
| Stabilité | Équilibrage des masses articulaires | 1 | 1 = méthode EFT, la plus stable. |
| | Convention des drives articulaires | Auto | Auto = vérifiée pendant les premières morts du raid (message dans le journal). |
| | Itérations du solveur PhysX | 12 | |
| Debug | Journalisation détaillée | false | Chronologie motrice de chaque mort. |

---

## Vérifier que ça fonctionne (premier test)

Dans `BepInEx/LogOutput.log`, au chargement :

```
Brutality : suivi des blessures sur N implémentation(s) de ApplyDamageInfo.
Brutality : Smoothie détecté — son arrêt anticipé des cadavres est neutralisé ...
Brutality 0.3.0 chargé.
```

Si ces lignes manquent, le plugin ne s'est pas activé : cherche « Brutality : configuration invalide »
ou « échec de l'initialisation » dans le journal.

Puis à chaque mort :

```
Brutality : <nom> mort — Thoracic, zone Chest/RibcageUp, 545x39 (intermédiaire, poussée ×1,12), 1612 J, 3,2 N·s, masse 83 kg (+11,4 kg équipement), posture Stand, chute : main tendue
```

Pour diagnostiquer, active « Journalisation détaillée » : chaque programme moteur déclenché est
alors listé avec son instant et son intensité, ainsi que les impulsions neutralisées.

Au début du raid, la sonde PhysX doit écrire une ligne du type
`Brutality : sonde PhysX — drive 30.0° (cible 30°) → normal ; butée X 60.0° → normale.`
Si elle indique « indéterminé », envoie le journal.

Messages à surveiller :
- `ragdoll non pris en charge (segment manquant…)` : squelette inattendu, le corps reste vanilla.
- `aucun pas Physics.Simulate intercepté` : mode de repli, moins précis.
- `erreur de simulation` : le corps repasse en ragdoll passif ; merci de transmettre le journal.

---

## Tests hors jeu

Les conventions anatomiques et la fidélité au portage Delirium sont couvertes par deux bancs de test autonomes qui compilent directement les vraies sources C# du plugin sans le jeu :

```bash
dotnet run --project tests/Brutality.KinematicsTests
dotnet run --project tests/Brutality.DeliriumTests
```

La fiche de preuves générée automatiquement (**77 / 77** vérifications : lois Delirium, simulation 60 Hz, audit statistique de diversité sur 60 morts, cinématique ISB, IK et configuration BepInEx) est consultable dans [`docs/VERIFICATION.md`](docs/VERIFICATION.md), accompagnée d'empreintes SHA-256 et de scénarios reproductibles dans `demo/banc-essai.html`.

Pour régénérer `docs/VERIFICATION.md` localement depuis une exécution fraîche :

```bash
dotnet run --project tests/Brutality.DeliriumTests -- --report docs/VERIFICATION.md
dotnet run --project tests/Brutality.KinematicsTests -- --append-report docs/VERIFICATION.md
```

Un workflow GitHub Actions prêt à l'emploi (`.github/workflows/verify.yml`) exécute également ces deux suites sur Linux sans aucune DLL propriétaire et publie la fiche en artefact / résumé de build.

---

## Architecture

```
src/Brutality/
  Plugin.cs, Settings.cs, Compat.cs        point d'entrée BepInEx, configuration, compatibilité
  Anatomy/AnatomyData.cs                   segments, articulations, de Leva, amplitudes, couples
  Core/
    JointKinematics.cs                     angles anatomiques (fonctions pures, testées hors jeu)
    BodyModel.cs                           segments, repères, masses/inerties, joints, état
    JointRig.cs                            remplacement des joints EFT par des joints anatomiques PhysX
    DriveController.cs                     muscles en drives PhysX : activation, Hill, couplage bi-articulaire
    DeathContext.cs                        analyse de la mort : mécanisme, balistique, santé, équipement
    Wounds.cs                              historique des blessures, base des munitions
    Impacts.cs                             impulsions balistiques et de souffle, variation par classe de munition
    BrutalityRagdoll.cs                    cycle de vie du cadavre, horloge physique, arrêt ≤ 10 s
    VarietyDirector.cs                     mémoire anti-répétition des plans et des gestes, palette propre à chaque mort
  Neural/
    MechanismProfiles.cs                   chronologies par mécanisme lésionnel
    NeuralController.cs                    construction des programmes et ordonnanceur d'agonie
    Programs.cs, Motor.cs                  programmes moteurs, fusion des commandes, IK
    Postures.cs                            décortication, crises de posturing, rigidité, signe de Lazare
    AgonyMoves.cs                          réaction à l'impact, respiration, dernier soupir, gestes d'agonie élaborés
  Patches/BrutalityPatches.cs              Physics.Simulate, Player.OnDead, ApplyDamageInfo, TryPutToSleep
tests/Brutality.KinematicsTests/           banc de test cinématique hors jeu (46 tests)
tests/Brutality.DeliriumTests/             preuves formelles de fidélité Delirium et diversité (31 tests)
docs/SCIENCE.md                            correspondance corpus scientifique → implémentation
docs/EUPHORIA.md                           architecture du portage Delirium
docs/VERIFICATION.md                       fiche de preuves formelles générée par les tests (77 tests)
.github/workflows/verify.yml               workflow CI GitHub Actions (77 tests sans DLL du jeu)
demo/banc-essai.html                       studio de démonstration 3D interactif
```

## Limites de la version 0.3.0
- Pas encore testée en jeu : les ordres de grandeur (tonus, gains, intensités) seront à ajuster
  après le premier test, via la configuration ou les tables de `AnatomyData.cs` et
  `MechanismProfiles.cs`.
- Les pieds et les mains ne sont pas des segments simulés (absents du ragdoll EFT) : ils sont
  fusionnés avec la jambe et l'avant-bras.
- Pas de masse oscillante pour les tissus mous (piste du corpus, §10). Prévue pour une version
  ultérieure.
- Moteur Euphoria et simulation permanente des cadavres (sources et correspondance des paramètres :
  `docs/EUPHORIA.md`) : non testés en jeu. Avec la simulation permanente, garder les ragdolls
  HollywoodFX désactivés.
- Les modes « Brutality » et « Mixte » de la 0.1.0 ont été retirés : la configuration ne propose plus
  « Moteur des animations ». Euphoria est le moteur définitif.
