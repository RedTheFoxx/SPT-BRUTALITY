# Du corpus scientifique au code

Ce document met en regard chaque couche du corpus
(`Corpus scientifique — biomécanique pour un ragdoll ultra réaliste.md`) et son implémentation
dans Brutality 0.2.0. Les valeurs chiffrées sont des ordres de grandeur adultes masculins : à
recalibrer après les tests en jeu.

## 1. Masse et inertie — de Leva (1996)

`Anatomy/AnatomyData.cs` → `Inertia`, appliqué dans `Core/BodyModel.cs` → `ApplyAnthropometry`.

| Segment Brutality | Composition | Fraction de masse | CdM (depuis proximal) | Rayons de giration (sag / trans / long) |
|---|---|---|---|---|
| Bassin | tronc inférieur + ½ tronc moyen | 19,34 % | auto (colliders) | auto |
| Thorax | tronc supérieur + ½ tronc moyen (+ équipement) | 24,12 % | auto, décalé par le sac et les plaques | auto |
| Tête | tête-cou (+ casque) | 6,94 % | centre des colliders crâniens | auto |
| Bras | bras | 2,71 % | 57,72 % | 28,5 / 26,9 / 15,8 % |
| Avant-bras | avant-bras + main | 2,23 % | 68 % | 44 / 43 / 12 % |
| Cuisse | cuisse | 14,16 % | 40,95 % | 32,9 / 32,9 / 14,9 % |
| Jambe | jambe + pied | 5,70 % | 60 % | 36 / 36 / 11 % |

Le ragdoll EFT n'a pas de rigidbody pour les mains et les pieds. Pour les segments fusionnés, le
centre de masse et les rayons de giration sont recalculés par le théorème de Huygens à partir des
valeurs de de Leva (main 0,61 %, pied 1,37 %). Le tenseur d'inertie est orienté sur l'axe
longitudinal réel de l'os, mesuré sur le squelette au moment de la mort
(`inertiaTensorRotation`). La variabilité de masse corporelle suit une loi normale, dans
l'esprit d'ANSUR II.

## 2. Articulations et repères — ISB (Wu 2002, 2005), Grood & Suntay

`Core/JointKinematics.cs`, testé hors jeu dans `tests/Brutality.KinematicsTests`.

Chaque segment porte un repère anatomique (R droite, U proximal/supérieur, F antérieur), construit
à partir des centres articulaires : axe des hanches, axe des épaules, direction os à os.

Les angles sont des décompositions de type ISB :

- **Rotules descendantes** (hanche, épaule) : direction distale de l'enfant dans le repère du
  parent. La flexion est l'angle dans le plan sagittal, l'abduction l'angle hors de ce plan (en
  miroir gauche/droite). La rotation axiale est obtenue par décomposition *swing-twist*.
- **Rotules montantes** (rachis, cou) : même principe sur l'axe supérieur de l'enfant ; la
  rotation axiale est absolue, avec un décalage mesuré à la mort.
- **Charnières** (genou, coude) : l'axe de flexion est déduit du plan des deux segments, orienté
  pour que la flexion soit positive (le genou porte la jambe en arrière, le coude porte
  l'avant-bras en avant). S'y ajoutent un varus/valgus et une prono-supination.

La discontinuité ±180° est placée à l'opposé de la zone physiologique : une épaule à 190° reste
lue 190°.

Le couple de chaque degré de liberté est appliqué le long du **gradient angulaire numérique**
(∂φ/∂ω). Ce choix rend le modèle insensible aux conventions de signe et conserve le moment
cinétique, puisque chaque couple est appliqué +τ à l'enfant et −τ au parent.

## 3. Amplitudes et résistance passive

`Anatomy/AnatomyData.cs` → `BuildSpecs`, `Core/Musculoskeletal.cs`.

- Amplitudes : NASA Anthropometric Source Book (vol. I), Norkin & White, Panjabi & White pour le
  rachis thoraco-lombaire et cervical.
- Couple passif : τp = A·e^(−B(φ−φmin)) − A·e^(−B(φmax−φ)) + K0(φneutre − φ), forme double
  exponentielle de Yoon & Mansour (1982), Riener & Edrich (1999) et Hoang et al. (2005).
  Conformément au corpus (§10), il n'y a **pas de butée dure** dans la zone physiologique.
- Couplage bi-articulaire (corpus §10, Riener & Edrich) : éléments passifs ischio-jambiers
  (étirés par flexion de hanche + extension de genou, seuil 70°) et droit fémoral (extension de
  hanche + flexion de genou). Leur tension agit sur les deux articulations, avec des bras de
  levier relatifs 1 / 0,75 et 0,4 / 0,6.
- Les butées sont portées par des `ConfigurableJoint` PhysX dont la pose de référence est la pose
  de la mort. La butée de flexion vaut exactement [min − φ0, max − φ0], dans le sens mesuré en jeu
  par une sonde PhysX ; les butées d'abduction et de rotation (cône) couvrent l'amplitude autour
  de φ0. Elles sont molles : raideur 6·A·B (dérivée
  du couple exponentiel à la butée), amortissement 6 % de la raideur. Les `CharacterJoint` d'EFT,
  dont les limites sont relatives à la pose de la mort, sont supprimés.
- Le tonus passif de repos et le couplage bi-articulaire sont des termes de rappel ajoutés à la
  cible des drives.

## 4. Force musculaire — Anderson et al. (2007), Hill (1938)

`Core/Musculoskeletal.cs` → `MuscleTorque`.

Chaque degré de liberté actif possède deux groupes antagonistes (TPos / TNeg). Le couple maximal
vaut :

T = a · Tmax · f(φ) · g(φ̇)

- **f** : relation couple-angle en cloche, centrée sur le milieu de l'amplitude.
- **g** : relation force-vitesse de Hill. En concentrique, g = (1 − v/vmax)/(1 + v/(0,25·vmax)),
  avec une courbure a/F0 = 0,25. En excentrique, g plafonne vers 1,8.
- **a** : activation, du premier ordre (Zajac 1989, Thelen 2003), τ = 15 ms à l'activation et
  50 ms à la désactivation. L'intégration est exacte (exponentielle), donc inconditionnellement
  stable.

C'est le compromis coût/réalisme recommandé par le corpus (§10 : « actionneurs couple-angle-vitesse
avant des muscles de Hill complets »).

**Stabilité numérique** (Yeo et al. 2023). Les muscles sont des drives PhysX en accélération
(raideur a·ωn², amortissement 2ζωn√a), intégrés implicitement par le solveur : aucune oscillation
numérique, quel que soit le pas. Le couple maximal du drive vaut a·Tmax·f·g. La pose cible est
convertie en rotation par un solveur de moindres carrés amortis (Levenberg-Marquardt) sur le
jacobien numérique des angles anatomiques. Le calcul a lieu avant chaque `Physics.Simulate`.

## 5. Modèles musculo-squelettiques (OpenSim, Rajagopal 2016)

Les couples maximaux et les vitesses maximales retenus sont des ordres de grandeur compatibles
avec les actionneurs du haut du corps de Rajagopal et avec les surfaces de Frey-Law (2012).
L'extraction directe des paramètres OpenSim (§9.5 du corpus) est une piste pour une version
ultérieure.

## 6. Tissus mous et impacts — Gruber (1998), Nahum & Melvin

`Core/Impacts.cs`.

- **Balle** : transfert de la quantité de mouvement réelle p = m·v. La masse et la vitesse
  initiale viennent du gabarit EFT de la munition ; la perte de vitesse dépend de la distance.
  Gruber rappelle qu'un modèle rigide surestime déjà les efforts d'impact : le multiplicateur vaut
  donc 1 par défaut. L'impulsion vanilla de 150 N·s, ainsi que celle de HollywoodFX, sont
  neutralisées.
- **Souffle** : impulsion répartie selon masse/(1 + 2·distance), avec une composante ascendante
  (réflexion au sol) et une rotation désordonnée des segments distaux.
- **Sol** : friction peau/vêtements (μd = 0,8) et restitution quasi nulle.
- La masse oscillante des tissus mous (corpus §10) n'est pas encore implémentée.

## 7. Dynamique articulée temps réel — Featherstone, Geijtenbeek 2013, Lee 2009

PhysX résout les contraintes articulaires (Featherstone n'est pas réimplémenté). Brutality y
ajoute deux couches, à la manière des contrôleurs musculaires de Geijtenbeek :

- une couche de couples internes, appliqués par paires ;
- une couche neuronale qui produit des excitations dans le temps.

## 8. Physiopathologie de la mort (couche neuronale)

`Neural/MechanismProfiles.cs`, `Neural/Programs.cs`.

- **Extinction du tonus** par groupes musculaires (membres inférieurs, tronc, cou, membres
  supérieurs), avec une latence et une constante de temps propres au mécanisme.
- **Posturing** de décortication après lésion encéphalique (la décérébration a été retirée du mod). **Réponse d'escrime**
  (fencing response, Hosseini & Lifshitz 2009).
- **Section médullaire** : paralysie flasque sous le niveau lésionnel (cervical, thoracique,
  lombaire).
- **Arrêt circulatoire** : myoclonies anoxiques, gasps agoniques. **Syncope convulsive** :
  myoclonies brèves.
- **Défense abdominale**, main portée sur la plaie (cinématique inverse à deux segments), repli
  fœtal, extension tonique, pédalage, ruade, roulade, tremblements de 6 à 10 Hz.
- Tous les paramètres sont tirés aléatoirement dans des intervalles. Les événements d'agonie
  suivent un processus de Poisson dont l'intensité décroît jusqu'à s'éteindre au plus tard 1,7 s
  avant l'arrêt forcé à 10 s.
