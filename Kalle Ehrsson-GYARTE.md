# Hemligheten bakom en värld som aldrig laggar

## En undersökning av metoder för att dela upp terräng och texturer i lokalt streamade tiles för att stabilisera bildfrekvens och reducera minnesanvändning

Av: Kalle Ehrsson  
Handledare: [Emil Südow](mailto:emil.sudow@ga.lbs.se)  
LBS gymnasium | Teknik | Design och Produktutveckling  
Gymnasiearbete  
Ämne | Vårtermin 2026

# Sammanfattning {#sammanfattning}

Denna undersökning behandlar hur en terrängbaserad miljö i Unity kan delas upp i lokalt streamade tiles för att uppnå stabilare bildfrekvens och minskad minnesanvändning. I stora spelvärldar leder statisk inladdning av hela miljön till hög och konstant belastning av systemets resurser. Syftet med arbetet var därför att utveckla och testa ett tile-baserat streaming-system där terräng och tillhörande resurser laddas in och ur under runtime beroende på spelarens position.

Metoden bestod av att jämföra två versioner av samma miljö: en utan streaming, där hela världen var laddad samtidigt, och en med aktiv lokal streaming via additiv scenhantering och asynchronous loading. Ett eget loggingsystem samlade kontinuerligt in data om FPS, CPU- och GPU frame time samt RAM-användning, vilket möjliggjorde en rättvis jämförelse mellan systemen.

Resultatet visade att den streamade miljön gav jämnare bildfrekvens och bättre kontroll över minnesanvändningen. Genom att endast hålla närliggande tiles aktiva minskade resursanvändningen, vilket förbättrade stabiliteten. Slutsatsen är att tile-baserad lokal streaming, kombinerad med tydlig scenhantering, är en effektiv metod för att bygga större Unity-miljöer med stabil prestanda.

# Abstract {#abstract}

This study examines how a terrain-based environment in Unity can be divided into locally streamed tiles in order to achieve a more stable frame rate and reduced memory usage. In large game worlds, static loading of the entire environment leads to high and constant strain on the system’s resources. The purpose of this project was therefore to develop and test a tile-based streaming system where terrain and associated resources are loaded in and out during runtime depending on the player’s position.

The method consisted of comparing two versions of the same environment: one without streaming, where the entire world was loaded simultaneously, and one with active local streaming through additive scene management and asynchronous loading. A custom logging system continuously collected data on FPS, CPU and GPU frame time, as well as RAM usage, which enabled a fair comparison between the systems.

The results showed that the streamed environment provided a more consistent frame rate and better control over memory usage. By keeping only nearby tiles active, resource consumption was reduced, which improved overall stability. The conclusion is that tile-based local streaming, combined with clear scene management, is an effective method for building larger Unity environments with stable performance.

# Innehållsförteckning {#innehållsförteckning}

**[Sammanfattning	2](#sammanfattning)**

[**Abstract	3**](#abstract)

[**Innehållsförteckning	4**](#innehållsförteckning)

[**1\. Syfte och bakgrund	5**](#1.-syfte-och-bakgrund)

[**2\. Metod och material	5**](#2.-metod-och-material)

[**3\. Resultat	7**](#3.-resultat)

[**4\. Diskussion	7**](#4.-diskussion)

[**5\. Slutsats	8**](#5.-slutsats)

[**6\. Källförteckning	8**](#6.-källförteckning)

# 1\. Syfte och bakgrund {#1.-syfte-och-bakgrund}

I denna rapport undersöks hur en terrängbaserad miljö i Unity kan delas upp för att skapa stabil bildfrekvens och minska belastningen på minnesanvändning. I stora spelvärldar blir det snabbt resurskrävande att ha hela miljön laddad samtidigt, vilket gör att mer strukturerad hantering av data blir nödvändigt. En metod för detta är att dela upp terrängen i mindre delar, så kallade tiles, som endast är aktiva när spelare befinner sig i närheten.

Denna metod bygger på lokal streaming, vilket innebär att allt innehåll laddas in och ur minnet under runtime beroende på spelarens position. För att detta ska fungera krävs tydlig scenhantering, där separata delar av världen organiseras i egna scener som kan laddas additivt och vid behov asynchronous för att undvika avbrott i rendering och uppdateringsloop.

För att förstå hur systemet är uppbyggt behöver några centrala begrepp förtydligas:

- ***Tile*** är en avgränsad del av terrängen som kan laddas in och ur självständigt.  
- ***Terrain Mesh*** är den geometriska modellen av terrängen som formar markens höjd och struktur, i mitt fall en sektion av världen.  
- ***Lokal streaming*** innebär att endast närliggande delar av världen är aktiva i minnet.  
- ***Runtime*** är den tid då spelet körs och resurser hanteras i realtid.  
- ***Scenhantering*** är systemet som styr hur olika delar av världen organiseras och aktiveras.  
- ***Additiv scenladdning*** innebär att flera scener kan vara aktiva samtidigt.  
- **Asynchronous loading** innebär att resurser laddas i bakgrunden utan att pausa huvudtråden eller frysa spelet.  
- ***Bildfrekvens (FPS)*** är antalet bildrutor per sekund och används för att mäta stabilitet.  
- ***Frame time*** är tiden det tar att rendera en bildruta.  
- ***Minnesanvändning (RAM)*** är mängden arbetsminne som spelet använder under körning.

Syftet med denna undersökning är att ta fram och testa en tile-baserad lokal streaming för terräng i Unity. Fokus ligger på att dela upp terrain meshes och texturer i separata tiles som laddas in och ut beroende på spelarens position samt att jämföra prestandan med en icke streamad miljö.

Arbetet utgår från frågeställningen:  
**Hur kan en terrängbaserad miljö i Unity delas upp i terräng tiles för att upprätthålla stabil bildfrekvens och minska minnesanvändning?**

# 2\. Metod och material {#2.-metod-och-material}

2.1 Metod  
Undersökningen genomfördes genom att skapa en terrängbaserad miljö i Unity, där endast streaming-systemet aktiverades eller avaktiverades mellan testkörningarna. Testets utgångspunkt bestod av att hela terrängen var inladdad utan någon form av dynamisk in- eller urladdning. I den streamade varianten användes samma terrängtiles, världslayout och texturdata, men med ett aktivt tile-baserad lokalt streaming-system som vid runtime utförde in- och urladdning beroende på spelarens position. På så sätt kunde prestandaskillnader isoleras till effekten av streaming-systemet och inte skillnader i miljökonfiguration

För att mäta prestanda användes ett fristående loggningssystem utvecklat för projektet. Systemet samlade kontinuerligt data en gång per sekund, bland annat FPS, CPU frame time, GPU frame time samt total RAM-användning. Varje stickprov fick en tidsstämpel i samband med testets start och skrevs löpande till en CSV-fil. Efter varje test genererades även en sammanfattning av alla mätvärden, inkluderande medelvärde, min- och maxvärden för respektive kategori, vilket möjliggjorde en mer exakt analys.

Testerna genomfördes på identisk hårdvara och med oförändrade grafikinställningar. Systemet exkluderar de första sekunderna av mätdata för att eliminera påverkan av uppstartsspikar från spelmotorns stabilisering. Metodens utformning gör undersökningen reproducerbar, då både miljö, parametrar och mätprocedur är tydligt definierade och kan upprepas med samma förutsättningar.

2.2 Material   
Projektets material bestod av Unity, en terräng uppdelad i separata tiles, texturer till varje tile och ett system för in- och urladdning under runtime. Den testade världen bestod av 22 x 22 terrängtiles. Totalt motsvarade detta en världsyta på 25 000 km2. Varje tile motsvarade en sektion av terrängen med egen terräng data och tillhörande resurser (t.ex. objekt och texturer). Tile-indelningen användes både i den icke-streamade och den streamade versionen för att göra jämförelsen rättvis. För att styra vad som var aktivt användes scenhantering där närliggande tiles var laddade, medan tiles längre på avstånd var urladdade. Detta gav en tydlig struktur för att analysera skillnaden mellan en streamad och en icke-streamad miljö.  
Som stöd användes officiell dokumentation från Unity om additiv scenhantering och Asynchronous Loading. Dessa källor valdes eftersom de beskriver hur Unity hanterar resurser i runtime och är därför relevanta för projektet. Källkritik gjordes genom att prioritera Unitys egna källor över offentliga diskussioner.

Källornas bidrag i arbetet var främst att ge riktlinjer för hur Unity hanterar scenladdning och resurser under runtime. Dokumentationen för LoadSceneMode.Additive, SceneManager.LoadSceneAsync och SceneManager.UnloadSceneAsync användes konkret vid implementationen av tile-systemet. Utifrån dessa byggdes en struktur där varje terrängtile sparades som en egen scen som kunde laddas in additivt när spelaren närmade sig och avladdas när den låg utanför en bestämd radie.

Dokumentationen gav alltså den tekniska förståelsen för hur in- och urladdning fungerar i Unity, men inte hur ett komplett streaming-system bör organiseras. Logiken för hur spelarens position kopplades till vilka tiles som skulle vara aktiva utvecklades därför genom egen design och testning.

# 2.3 Projektplan Projektet planerades i fyra delar: insamling av information, implementation, testning och analys. I första fasen definierades frågeställningen. Därefter skapades en basversion av världen utan streaming för att fungera som referens. I implementationsfasen byggdes en tile-baserad streaming-system där runtime loading kopplades till spelarens position och där scenhantering ansvarade för in- och urladdning av tiles.

I testfasen kördes båda versionerna under samma förhållanden och spelaren rörde sig genom miljön. Datan dokumenterades i löpande loggar. Analysfasen fokuserade på att jämföra mönster mellan den streamade och icke-streamade versionen, särskilt kring bildfrekvensens stabilitet och minnesanvändning.

# 3\. Resultat {#3.-resultat}

Resultatet baseras på uppmätta prestandadata. Mätningar genomfördes på två olika system:

| Komponent | Desktop | Laptop |
| :---- | :---- | :---- |
| CPU | Intel(R) Core(TM) i5-10400F | Intel(R) Core(TM) i5-1235U |
| GPU | AMD Radeon RX 6600 | GeForce MX570 A |
| RAM | 32 GB DDR4 2666 MT/s | 16 GB DDR4 3200 MT/s |
| Lagring | NVMe SSD (2100 MB/s läs, 1700 MB/s skriv) | NVMe SSD (2500 MB/s läs, 1600 MB/s skriv) |
| Upplösning vid test | 1920x1080p | 1920x1080p |

Samtliga mätningar genomfördes med identiska grafikinställningar och samma upplösning på båda systemen. Inga bakgrundsprocesser utöver operativsystemets standardtjänster var aktiva under testkörningarna.

Undersökningens primära fokus var att analysera skillnaden mellan en icke-streamad och streamad miljö. Användningen av två system syftade till att erhålla jämförbara mätvärden under skilda hårdvaruförutsättningar.

Det är viktigt att notera att samtliga mätvärden påverkas av maskinvaran. Processor, grafikkort, RAM och lagringshastighet påverkar både bildfrekvens och minnesanvändning. Resultaten ska därför tolkas som relativa jämförelser mellan system under identiska testförhållanden.

# 3.1 Icke-streamad miljö

I den icke-streamade versionen laddades hela världen vid start. Alla terrängtiles, objekt och detaljer var aktiva samtidigt, oavsett spelarens position. Detta medförde en konstant resursbelastning.

## Uppmätta värden

**Desktop:**

* Genomsnittlig FPS: 53  
* Lägsta uppmätta FPS: 46  
* RAM-användning: 1.75 GB

**Laptop:**

* Genomsnittlig FPS: 27  
* Lägsta uppmätta FPS: 13  
* RAM-användning: 2.2 GB

Skillnaden mellan systemen blev tydlig i denna variant. Den svagare hårdvaran påverkas mer negativt av att hela världen var aktiv samtidigt.

# 3.2 Streamad miljö (tile-baserad)

I den streamade versionen delades världen upp i separata tiles som laddades in och ur beroende på spelarens position.

Inladdning skedde additivt med [LoadSceneMode.Additive](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.LoadSceneMode.Additive.html) och urladdning genom [SceneManager.UnloadSceneAsync](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.SceneManager.UnloadSceneAsync.html). Urladdningen sker asynchronous, vilket innebär att resurser tas bort i bakgrunden utan att spelet fryser.

## Uppmätta värden

**Desktop:**

* Genomsnittlig FPS: 128  
* Lägsta uppmätta FPS: 48  
* RAM-användning: 1.6 GB

**Laptop:**

* Genomsnittlig FPS: 60  
* Lägsta uppmätta FPS: 43  
* RAM-användning: 2 GB


Förbättring observeras på båda systemen. Den relativa ökningen i FPS var större på laptop-systemet, vilket indikerar att streaming-metoden ger störst effekt på mer begränsad hårdvara.

# 3.3 Grafisk presentation av resultat

Resultaten sammanställdes i stapeldiagram för att tydliggöra skillnaderna mellan den icke-streamade och den streamade implementationen på de två testsystemen.

![][image1]

**Figur 1\.** Genomsnittlig FPS och lägsta FPS i icke-streamad och streamad miljö på Desktop-systemet. Diagrammet visar den tydliga ökningen i genomsnittlig bildfrekvens vid användning av tile-baserad streaming.

![][image2]  
**Figur 2\.** Genomsnittlig FPS och lägsta FPS i icke-streamad och streamad miljö på Laptop-systemet. Resultatet visar att streaming-metoden ger en tydlig förbättring av den genomsnittliga prestandan även på svagare hårdvara.

# 3.4 Analys och åtgärd av minneshantering vid scenurladdning

Under implementationen identifierades ett problem där tiles inte konsekvent avlägsnades från minnet efter att spelaren lämnat det aktuella området. Detta resulterade i en gradvis ökning av minnesanvändningen trots att streaming-systemet var aktivt.

Genom analys av Unitys dokumentation för [SceneManager.UnloadSceneAsync](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.SceneManager.UnloadSceneAsync.html) framgick att urladdningen sker asynchronous. Det framkom även att kvarvarande referenser till objekt i den urladdade scenen kan förhindra att minnet frigörs korrekt.

Åtgärderna bestod av följande:

* Eliminering av globala referenser till objekt i den urladdade scenen  
* Säkerhetsställande av att den asynchronous urladdning operationen slutfördes innan nya tiles initierades  
* Verifiering av minnesförändring efter urladdning.

Efter implementering av dessa justeringar stabiliserades RAM-användningen och uppvisade inte längre en successiv ökning över tid.

# 4\. Diskussion {#4.-diskussion}

Resultatet stödjer projektets utgångspunkt att lokal streaming kan ge stabilare teknisk helhet i stora terrängmiljöer. Den centrala förklaringen är att tile-baserad lokal streaming begränsar mängden innehåll som är laddat samtidigt.  
När runtime loading styrs av spelarens position minskar konkurrensen om resurser mellan delar av världen som inte behöver vara tillgängliga samtidigt. Detta skapar bättre förutsättningar för stabil bildfrekvens eftersom arbetsbelastningen fördelas mer jämnt.

Analysen av resultaten visar också att scenhantering är avgörande för effekten. Streaming i sig räcker inte om gränserna mellan tiles hanteras otydligt. Prioriteringen av laddning och urladdning är otydlig. Ett välfungerande system kräver att logiken för in- och urladdning är konsekvent, annars finns risk för synliga övergångar, sen inläsning eller onödig omladdning. Stabiliteten förbättras alltså inte enbart av att data delas upp, utan av hur uppdelningen samverkar med alla scenens system.

Systemets begränsning framträder främst vid komplexa miljöer där flera tunga system samverkar, exempelvis avancerad AI, fysik och nätverk i samma område. I sådana fall kan streaming minska minnesanvändningen men ändå behöva kompletteras med ytterligare optimering för att bevara jämn respons. En annan begränsning är att utvecklingskostnaden ökar, eftersom tile-indelning, beroende mellan resurser och test av övergångar, kräver mer planering än en statisk scen.

Ur ett skalbarhetsperspektiv är metoden stark. Samma princip kan utökas till större världar genom flera tiles, hierarkisk indelning och tydliga regler för prioriterad runtime loading. Detta innebär att lösningen är relevant inte bara för denna prototyp utan även för framtida projekt med större innehåll. Samtidigt blir kvalitetskraven högre när världen växer, eftersom fel i scenhantering får större konsekvenser över flera delar av världen.

Återkopplat till frågeställningen visar undersökningen att en terrängbaserad Unity-miljö kan delas upp i lokalt streamade tiles på ett sätt som förbättrar stabilitet och minskar minnesbelastning, förutsatt att tile-based streaming kombineras med genomtänkt scenhantering och kontrollerad runtime loading.

# 5\. Slutsats {#5.-slutsats}

En terrängbaserad miljö i Unity kan delas upp i lokalt streamade tiles genom tile-baserad streaming och in- och urladdning kopplad till spelarens position. Denna struktur, tillsammans med tydlig scenhantering, ger stabilare bildfrekvens och lägre minnesanvändning än en icke-streamad scen.  
Därmed är streaming en effektiv metod för att bygga större världar med mer stabil drift.

# 6\. Källförteckning {#6.-källförteckning}

Unity Technologies. LoadSceneMode.Additive. (2026).  
[https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.LoadSceneMode.Additive.html](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.LoadSceneMode.Additive.html)  
(Hämtad 2026-02-18).

Unity Technologies. SceneManager.LoadSceneAsync. (2026).  
[https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.SceneManager.LoadSceneAsync.html](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.SceneManager.LoadSceneAsync.html)  
(Hämtad 2026-03-3).

Unity Technologies. SceneManager.UnloadSceneAsync. (2026).  
[https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.SceneManager.UnloadSceneAsync.html](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.SceneManager.UnloadSceneAsync.html)  
(Hämtad 2026-02-18).

[image1]: <data:image/png;base64,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>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAXoAAADpCAYAAAAqJfzJAAAQS0lEQVR4Xu3dW4iV5d/G8SnKIeioOrLIOuiooA6i8sCgEZEOqtOXyDJ66+zFg0iFsA1UUhARBdHGXmxjRbz1ikkdSCVWUlh4UIGJCVm0343TxkyfP/cT99OzrpnJNf5G87rv7wcu1rNZa5r89btcDv+/a6QBABRtRC8AAMpC0QNA4Sh6ACgcRQ8AhaPoAaBwFD0AFI6iB4DCUfQAUDiKHgAKR9EDQOEoegAoHEUPAIWj6AGgcDMu+l9//bW55ZZb9HLYvn379BIAYBYcE0X/+OOPNw899JBeBgDMAooeAAo360Wf7vXTv673k48//njStS1btgxcO3jwYHv9/fffb+67775mxYoV3b30/QAApjerRf/mm282H374YXf+3HPPTSr4LBV2Ptd39P3nffPNN915Kvp0nK4lP/3007TfCwDgL7Na9MmePXuaRx99dOAdeZIe//jjj4HnTlX0r776ajM+Pt5/WrNy5cr2N41U9OndfN8/fS8AgFku+k2bNrX33nnnnfb8+++/Hyh6NVXRv/jii92ParI77rijWbNmTVv099xzz8C9qb4uAOBvs1r0ev2FF14YKPrNmzcP3M/3nnjiia7of/jhh+app57qP6193q5du7of3eg9AMD0DrvoNcntt9/ePPjgg83+/fubtWvXDtzLx6msv/vuu/Z448aN7b2XXnqpPU9/AsjPXb9+ffujnltvvbX7Grno08/3//zzz2bVqlUUPQAcwqwWfZKKPv2oZefOne15v+i//vrrZt26dc1tt93WTExMdK9J7r///ubOO+/szp988sn2Z/Ppf4GT5aLfvXt3e+/111/v7gEApjbjoj9cuegjpvrRDQDgn1H0AFC4o1b0AIB/B0UPAIWj6AGgcBQ9ABSOogeAwlH0AFA4ih4ACkfRA0DhKHoAKBxFj6H914P/N22OdSOLrp82QOkoegxNy32Yor/qqquauXPntrn77rvba+n4aNNyH6bop/s+lyxZopemlD5AJ/+7n3HGGe2133//XZ4FHHkUPYam5T5M0c+bN687Pv3009vHVHzpIyfT40UXXdT+baSfffZZc+6553bPTX9r6VlnndV90Ez6vIILLrigeeWVV5rnn3++Of/887vnDkPLfaZFP3/+/Oa9995rj3PR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>