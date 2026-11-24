# Scientific Corpus — Biomechanics for an Ultra-Realistic Ragdoll

A corpus of reliable sources on human body physics (mass, joints, range-of-motion limits, muscles, tissues, impacts), organized by ragdoll layer. Updated 2026-11-18.

**Legend**: ✓ = reference confirmed by web search at the time of writing. Without ✓ = from the author's knowledge: verify the DOI before citing.

## 1. Overview: Which Source for Which Layer

- **Mass and inertia**: de Leva 1996, Dumas 2007, Yeadon 1990, NASA tables.
- **Joints and coordinate frames**: ISB recommendations (Wu 2002, 2005), Kapandji, Panjabi & White for the spine.
- **Range-of-motion limits and passive resistance**: exponential passive torque models, NASA Source Book, Norkin & White.
- **Muscle force**: torque-angle-velocity surfaces, Hill models (Zajac, Thelen, Millard).
- **Ready-to-use anatomical data**: OpenSim models (Rajagopal 2016, Arnold 2010).
- **Soft tissues, failure, impacts**: Gruber 1998, Yamada, Nahum & Melvin, THUMS, Hybrid III.
- **Real-time articulated dynamics**: Featherstone, Geijtenbeek 2013, Lee et al. 2009.

## 2. Segment Mass and Inertia

- ✓ **de Leva P. (1996)**, *Adjustments to Zatsiorsky-Seluyanov's segment inertia parameters*, J. Biomechanics 29(9):1223-1230. DOI 10.1016/0021-9290(95)00178-6. Standard reference: relative masses, center-of-mass positions, and radii of gyration, male and female, referenced to joint centers. Body mass fractions (M / F): head 0.0694 / 0.0668; trunk 0.4346 / 0.4257; upper arm 0.0271 / 0.0255; forearm 0.0162 / 0.0138.
- **Dumas R., Chèze L., Verriest J.-P. (2007)**, *Adjustments to McConville et al. and Young et al. body segment inertial parameters*, J. Biomechanics 40:543-553. Alternative with full inertia tensors.
- **Yeadon M.R. (1990)**, *The simulation of aerial movement — II. A mathematical inertia model of the human body*, J. Biomechanics 23:67-74. Geometric model for morphology-customized inertia.
- **Hatze H. (1980)**, *A mathematical model for the computational determination of parameter values of anthropomorphic segments*, J. Biomechanics 13:833-843.
- **Winter D.A.**, *Biomechanics and Motor Control of Human Movement*, Wiley, 4th ed. 2009. Foundational textbook, includes Dempster tables.
- ✓ **NASA Anthropometric Source Book** (Webb Associates, 1978, 3 vols.). Anthropometry and joint ranges of motion.
- **ANSUR II** (Gordon et al., US Army, 2012). Modern anthropometric dataset for generating varied morphologies.

## 3. Joints and Kinematics

- ✓ **Wu G. et al. (2002)**, ISB recommendation on joint coordinate systems — part I: ankle, hip and spine, J. Biomechanics 35:543-548. DOI 10.1016/S0021-9290(01)00222-6.
- ✓ **Wu G. et al. (2005)**, ISB recommendation — part II: shoulder, elbow, wrist and hand, J. Biomechanics 38:981-992. DOI 10.1016/j.jbiomech.2004.05.042.
- **Grood E.S., Suntay W.J. (1983)**, A joint coordinate system for the clinical description of three-dimensional motions: application to the knee, J. Biomech. Eng. 105:136-144. Foundation of the ISB convention.
- **Kapandji A.I.**, *Physiologie articulaire* (Maloine, in French; English ed. *The Physiology of the Joints*). Joint mechanics, couplings, instantaneous centers of rotation.
- **Neumann D.A.**, *Kinesiology of the Musculoskeletal System*, Elsevier.
- **Panjabi M.M., White A.A.**, *Clinical Biomechanics of the Spine*, Lippincott. Stiffness and ranges of motion per vertebral level.

## 4. Range-of-Motion Limits and Passive Resistance

- **Reference ranges of motion**: NASA Anthropometric Source Book (vol. I) ✓; Norkin C.C., White D.J., *Measurement of Joint Motion: A Guide to Goniometry*, F.A. Davis.
- ✓ **Exponential passive torque model**: passive torques are classically modeled as exponential functions of joint angle (Yoon & Mansour 1982; Hoang et al. 2005). Preferred over hard stops: torque rises progressively near the limit.
- ✓ **Anderson D.E., Nussbaum M.A., Madigan M.L. (2010)**, A new method for gravity correction of dynamometer data and determining passive elastic moments at the joint, J. Biomechanics 43:1220-1223.
- **Riener R., Edrich T. (1999)**, Identification of passive elastic joint moments in the lower extremities, J. Biomechanics 32:539-544.
- ✓ **Anderson D.E., Madigan M.L., Nussbaum M.A. (2007)**, Maximum voluntary joint torque as a function of joint angle and angular velocity: model development and application to the lower limb, J. Biomechanics 40:3105-3113.
- **Frey-Law L.A. et al. (2012)**, Knee and elbow 3D strength surfaces: peak torque-angle-velocity relationships, J. Applied Biomechanics 28:726-737.

## 5. Muscle Models

- **Zajac F.E. (1989)**, Muscle and tendon: properties, models, scaling, and application to biomechanics and motor control, Crit. Rev. Biomed. Eng. 17:359-411. Foundational reference for the muscle-tendon model.
- ✓ **Thelen D.G. (2003)**, Adjustment of muscle mechanics model parameters to simulate dynamic contractions in older adults, J. Biomech. Eng. 125:70-77. Hill model with contractile, parallel, and series elements.
- ✓ **Millard M. et al. (2013)**, Flexing computational muscle: modeling and simulation of musculotendon dynamics, J. Biomech. Eng. 135(2):021005. Curves fitted to experimental data, more robust formulation. Preferred version.
- ✓ **Yeo S.-H., Verheul J., Herzog W., Sueda S. (2023)**, Numerical instability of Hill-type muscle models, J. R. Soc. Interface 20:20220430. Essential for real-time integration.
- ✓ Systematic review of Hill-type models (bioRxiv preprint 2022): literature map for choosing a variant.
- **Hill A.V. (1938)**, The heat of shortening and the dynamic constants of muscle, Proc. R. Soc. B 126:136-195. **Gordon A.M., Huxley A.F., Julian F.J. (1966)**, J. Physiology 184:170-192. Original force-velocity and force-length curves.

## 6. Full Musculoskeletal Models and Tools

- ✓ **Delp S.L. et al. (2007)**, OpenSim: open-source software to create and analyze dynamic simulations of movement, IEEE Trans. Biomed. Eng. 54:1940-1950.
- ✓ **Rajagopal A. et al. (2016)**, Full-body musculoskeletal model for muscle-driven simulation of human gait, IEEE Trans. Biomed. Eng. 63(10):2068-2079. 37 degrees of freedom, 80 muscle-tendon units for the legs, 17 torque actuators for the upper body. Open model and data on SimTK (simtk.org/home/full\_body).
- ✓ **Arnold E.M. et al. (2010)**, A model of the lower limb for analysis of human movement, Ann. Biomed. Eng. 38:269-279. Muscle architecture derived from 21 cadavers.
- **Saul K.R. et al. (2015)**, Benchmarking of dynamic simulation predictions in two software platforms using an upper limb musculoskeletal model, Comput. Methods Biomech. Biomed. Eng. 18:1445-1458.
- **Anderson F.C., Pandy M.G. (1999)**, A dynamic optimization solution for vertical jumping in three dimensions, Comput. Methods Biomech. Biomed. Eng. 2:201-231. Lower-limb muscle parameters.
- ✓ **Pandy M.G. (2001)**, Computer modeling and simulation of human movement, Annu. Rev. Biomed. Eng. 3:245-273. Review of integrated models.
- **Tools**: OpenSim; SCONE (Geijtenbeek 2019, J. Open Source Software); MyoSuite (Caggiano et al. 2022); AnyBody (Damsgaard et al. 2006).

## 7. Soft Tissues, Failure, and Impacts

- **Gruber K. et al. (1998)**, A comparative study of impact dynamics: wobbling mass model versus rigid body models, J. Biomechanics 31:439-444. Why a rigid-body model overestimates impact forces.
- **Nigg B.M., Herzog W.**, *Biomechanics of the Musculo-skeletal System*, Wiley, 3rd ed. 2007.
- **Fung Y.C.**, *Biomechanics: Mechanical Properties of Living Tissues*, Springer, 1993.
- **Yamada H.**, *Strength of Biological Materials*, Williams & Wilkins, 1970. Failure thresholds for bone, ligaments, tendons.
- **Nahum A.M., Melvin J.W.**, *Accidental Injury: Biomechanics and Prevention*, Springer.
- **THUMS** (Toyota) and **Hybrid III** (NHTSA documentation): automotive safety human models, useful for calibrating impact behavior.

## 8. Bridge to Real-Time Simulation

- **Featherstone R.**, *Rigid Body Dynamics Algorithms*, Springer, 2008. Articulated chain dynamics.
- **Lee S.-H., Sifakis E., Terzopoulos D. (2009)**, Comprehensive biomechanical modeling and simulation of the upper body, ACM Trans. Graphics 28(4).
- **Geijtenbeek T., van de Panne M., van der Stappen A.F. (2013)**, Flexible muscle-based locomotion for bipedal creatures, ACM Trans. Graphics (SIGGRAPH Asia) 32(6).

## 9. Recommended Reading Path

1. **Mass**: de Leva 1996, then Winter for context.
2. **Joints**: ISB Wu 2002 and 2005, then Kapandji.
3. **Limits**: exponential passive model (Riener & Edrich, Anderson 2010) and NASA Source Book for ranges.
4. **Muscles**: Zajac 1989, Millard 2013, then Yeo 2023 for numerical stability.
5. **Data**: Rajagopal model in OpenSim, to directly extract masses, axes, limits, and muscle parameters.
6. **Impacts**: Gruber 1998 then Yamada for failure thresholds.

## 10. Implementation Leads

- Replace hard stops with an exponential passive torque whose stiffness depends on the joint and the angle of neighboring joints (biarticular coupling).
- Model large muscle groups with torque-angle-velocity actuators (Anderson 2007) before moving to full Hill muscles: best cost/realism trade-off.
- Use Rajagopal 2016 parameters as initial values, then scale them by mass and height.
- Include a wobbling mass (soft tissues) for segments with a high soft-tissue ratio (thigh, trunk) if impacts matter.

## Limitations of This Corpus

- References without ✓ have not been re-verified online.
- Data from de Leva and NASA describe young adults (American/Caucasian population for Zatsiorsky): exercise caution for other body types, children, or older adults.
- Failure values (bones, ligaments) vary strongly with age, sex, and loading rate.
