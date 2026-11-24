# Corpus scientifique — biomécanique pour un ragdoll ultra réaliste

Corpus de sources fiables sur la physique du corps humain (masse, articulations, limites d'amplitude, muscles, tissus, impacts), organisé par couche d'un ragdoll. Mis à jour le 18/11/2026.

**Légende** : ✓ = référence confirmée par recherche web à la rédaction. Sans ✓ = issue de la connaissance de l'auteur : vérifier le DOI avant citation.

## 1. Vue d'ensemble : quelle source pour quelle couche

- **Masse et inertie** : de Leva 1996, Dumas 2007, Yeadon 1990, tables NASA.
- **Articulations et repères** : recommandations ISB (Wu 2002, 2005), Kapandji, Panjabi & White pour le rachis.
- **Butées d'amplitude et résistance passive** : modèles exponentiels de couple passif, NASA Source Book, Norkin & White.
- **Force musculaire** : surfaces couple-angle-vitesse, modèles de Hill (Zajac, Thelen, Millard).
- **Données anatomiques prêtes à l'emploi** : modèles OpenSim (Rajagopal 2016, Arnold 2010).
- **Tissus mous, rupture, impacts** : Gruber 1998, Yamada, Nahum & Melvin, THUMS, Hybrid III.
- **Dynamique articulée temps réel** : Featherstone, Geijtenbeek 2013, Lee et al. 2009.

## 2. Masse et inertie des segments

- ✓ **de Leva P. (1996)**, *Adjustments to Zatsiorsky-Seluyanov's segment inertia parameters*, J. Biomechanics 29(9):1223-1230. DOI 10.1016/0021-9290(95)00178-6. Référence standard : masses relatives, positions des centres de masse et rayons de giration, hommes et femmes, référencés aux centres articulaires. Fractions de masse corporelle (H / F) : tête 0,0694 / 0,0668 ; tronc 0,4346 / 0,4257 ; bras 0,0271 / 0,0255 ; avant-bras 0,0162 / 0,0138.
- **Dumas R., Chèze L., Verriest J.-P. (2007)**, *Adjustments to McConville et al. and Young et al. body segment inertial parameters*, J. Biomechanics 40:543-553. Alternative avec tenseurs d'inertie complets.
- **Yeadon M.R. (1990)**, *The simulation of aerial movement — II. A mathematical inertia model of the human body*, J. Biomechanics 23:67-74. Modèle géométrique pour une inertie personnalisée par morphologie.
- **Hatze H. (1980)**, *A mathematical model for the computational determination of parameter values of anthropomorphic segments*, J. Biomechanics 13:833-843.
- **Winter D.A.**, *Biomechanics and Motor Control of Human Movement*, Wiley, 4e éd. 2009. Livre de base, tables de Dempster incluses.
- ✓ **NASA Anthropometric Source Book** (Webb Associates, 1978, 3 vol.). Anthropométrie et amplitudes articulaires.
- **ANSUR II** (Gordon et al., US Army, 2012). Jeu de données anthropométriques moderne pour générer des morphologies variées.

## 3. Articulations et cinématique

- ✓ **Wu G. et al. (2002)**, ISB recommendation on joint coordinate systems — part I: ankle, hip and spine, J. Biomechanics 35:543-548. DOI 10.1016/S0021-9290(01)00222-6.
- ✓ **Wu G. et al. (2005)**, ISB recommendation — part II: shoulder, elbow, wrist and hand, J. Biomechanics 38:981-992. DOI 10.1016/j.jbiomech.2004.05.042.
- **Grood E.S., Suntay W.J. (1983)**, A joint coordinate system for the clinical description of three-dimensional motions: application to the knee, J. Biomech. Eng. 105:136-144. Base de la convention ISB.
- **Kapandji A.I.**, *Physiologie articulaire* (Maloine, en français). Mécanismes articulaires, couplages, centres instantanés de rotation.
- **Neumann D.A.**, *Kinesiology of the Musculoskeletal System*, Elsevier.
- **Panjabi M.M., White A.A.**, *Clinical Biomechanics of the Spine*, Lippincott. Rigidités et amplitudes par niveau vertébral.

## 4. Limites d'amplitude et résistance passive

- **Amplitudes de référence** : NASA Anthropometric Source Book (vol. I) ✓ ; Norkin C.C., White D.J., *Measurement of Joint Motion: A Guide to Goniometry*, F.A. Davis.
- ✓ **Modèle exponentiel du couple passif** : les couples passifs sont classiquement modélisés par des fonctions exponentielles de l'angle (Yoon & Mansour 1982 ; Hoang et al. 2005). À préférer à des butées dures : le couple monte progressivement près de la limite.
- ✓ **Anderson D.E., Nussbaum M.A., Madigan M.L. (2010)**, A new method for gravity correction of dynamometer data and determining passive elastic moments at the joint, J. Biomechanics 43:1220-1223.
- **Riener R., Edrich T. (1999)**, Identification of passive elastic joint moments in the lower extremities, J. Biomechanics 32:539-544.
- ✓ **Anderson D.E., Madigan M.L., Nussbaum M.A. (2007)**, Maximum voluntary joint torque as a function of joint angle and angular velocity: model development and application to the lower limb, J. Biomechanics 40:3105-3113.
- **Frey-Law L.A. et al. (2012)**, Knee and elbow 3D strength surfaces: peak torque-angle-velocity relationships, J. Applied Biomechanics 28:726-737.

## 5. Modèles de muscle

- **Zajac F.E. (1989)**, Muscle and tendon: properties, models, scaling, and application to biomechanics and motor control, Crit. Rev. Biomed. Eng. 17:359-411. Référence fondatrice du modèle muscle-tendon.
- ✓ **Thelen D.G. (2003)**, Adjustment of muscle mechanics model parameters to simulate dynamic contractions in older adults, J. Biomech. Eng. 125:70-77. Modèle de Hill à élément contractile, élément parallèle et élément série.
- ✓ **Millard M. et al. (2013)**, Flexing computational muscle: modeling and simulation of musculotendon dynamics, J. Biomech. Eng. 135(2):021005. Courbes ajustées sur données expérimentales, formulation plus robuste. Version à privilégier.
- ✓ **Yeo S.-H., Verheul J., Herzog W., Sueda S. (2023)**, Numerical instability of Hill-type muscle models, J. R. Soc. Interface 20:20220430. Essentiel pour l'intégration temps réel.
- ✓ Revue systématique des modèles de Hill-type (preprint bioRxiv 2022) : carte de la littérature pour choisir une variante.
- **Hill A.V. (1938)**, The heat of shortening and the dynamic constants of muscle, Proc. R. Soc. B 126:136-195. **Gordon A.M., Huxley A.F., Julian F.J. (1966)**, J. Physiology 184:170-192. Courbes force-vitesse et force-longueur d'origine.

## 6. Modèles musculo-squelettiques complets et outils

- ✓ **Delp S.L. et al. (2007)**, OpenSim: open-source software to create and analyze dynamic simulations of movement, IEEE Trans. Biomed. Eng. 54:1940-1950.
- ✓ **Rajagopal A. et al. (2016)**, Full-body musculoskeletal model for muscle-driven simulation of human gait, IEEE Trans. Biomed. Eng. 63(10):2068-2079. 37 degrés de liberté, 80 unités muscle-tendon pour les jambes, 17 actionneurs de couple pour le haut du corps. Modèle et données libres sur SimTK (simtk.org/home/full\_body).
- ✓ **Arnold E.M. et al. (2010)**, A model of the lower limb for analysis of human movement, Ann. Biomed. Eng. 38:269-279. Architecture musculaire issue de 21 cadavres.
- **Saul K.R. et al. (2015)**, Benchmarking of dynamic simulation predictions in two software platforms using an upper limb musculoskeletal model, Comput. Methods Biomech. Biomed. Eng. 18:1445-1458.
- **Anderson F.C., Pandy M.G. (1999)**, A dynamic optimization solution for vertical jumping in three dimensions, Comput. Methods Biomech. Biomed. Eng. 2:201-231. Paramètres musculaires du membre inférieur.
- ✓ **Pandy M.G. (2001)**, Computer modeling and simulation of human movement, Annu. Rev. Biomed. Eng. 3:245-273. Revue des modèles intégrés.
- **Outils** : OpenSim ; SCONE (Geijtenbeek 2019, J. Open Source Software) ; MyoSuite (Caggiano et al. 2022) ; AnyBody (Damsgaard et al. 2006).

## 7. Tissus mous, rupture et impacts

- **Gruber K. et al. (1998)**, A comparative study of impact dynamics: wobbling mass model versus rigid body models, J. Biomechanics 31:439-444. Pourquoi un modèle à corps rigides surestime les forces d'impact.
- **Nigg B.M., Herzog W.**, *Biomechanics of the Musculo-skeletal System*, Wiley, 3e éd. 2007.
- **Fung Y.C.**, *Biomechanics: Mechanical Properties of Living Tissues*, Springer, 1993.
- **Yamada H.**, *Strength of Biological Materials*, Williams & Wilkins, 1970. Seuils de rupture os, ligaments, tendons.
- **Nahum A.M., Melvin J.W.**, *Accidental Injury: Biomechanics and Prevention*, Springer.
- **THUMS** (Toyota) et **Hybrid III** (documentation NHTSA) : modèles humains de sécurité automobile, utiles pour calibrer le comportement à l'impact.

## 8. Passerelle vers la simulation temps réel

- **Featherstone R.**, *Rigid Body Dynamics Algorithms*, Springer, 2008. Dynamique des chaînes articulées.
- **Lee S.-H., Sifakis E., Terzopoulos D. (2009)**, Comprehensive biomechanical modeling and simulation of the upper body, ACM Trans. Graphics 28(4).
- **Geijtenbeek T., van de Panne M., van der Stappen A.F. (2013)**, Flexible muscle-based locomotion for bipedal creatures, ACM Trans. Graphics (SIGGRAPH Asia) 32(6).

## 9. Parcours de lecture conseillé

1. **Masse** : de Leva 1996, puis Winter pour le contexte.
2. **Articulations** : ISB Wu 2002 et 2005, puis Kapandji.
3. **Butées** : modèle exponentiel passif (Riener & Edrich, Anderson 2010) et NASA Source Book pour les plages.
4. **Muscles** : Zajac 1989, Millard 2013, puis Yeo 2023 pour la stabilité numérique.
5. **Données** : modèle Rajagopal dans OpenSim, pour extraire directement masses, axes, limites et paramètres musculaires.
6. **Impacts** : Gruber 1998 puis Yamada pour les seuils de rupture.

## 10. Pistes d'implémentation

- Remplacer les butées dures par un couple passif exponentiel à raideur dépendante de l'articulation et de l'angle des articulations voisines (couplage biarticulaire).
- Modéliser les gros groupes musculaires par des actionneurs à couple-angle-vitesse (Anderson 2007) avant de passer à des muscles de Hill complets : meilleur compromis coût/réalisme.
- Utiliser les paramètres de Rajagopal 2016 comme valeurs initiales, puis les mettre à l'échelle par la masse et la taille.
- Prévoir une masse oscillante (tissus mous) pour les segments à forte proportion de tissu mou (cuisse, tronc) si les impacts comptent.

## Limites de ce corpus

- Les références sans ✓ n'ont pas été revérifiées en ligne.
- Les données de de Leva et de la NASA décrivent des adultes jeunes (population américaine/caucasienne pour Zatsiorsky) : prudence pour d'autres morphologies, enfants ou personnes âgées.
- Les valeurs de rupture (os, ligaments) varient fortement selon l'âge, le sexe et la vitesse de chargement.
