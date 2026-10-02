# A Mockingbird Studio adatvédelmi nyilatkozata

Hatálybalépés dátuma: 2026. október 1.

## Helyi feldolgozás
A Mockingbird Studio az Ön számítógépén írja át a felvételeket és javítja az átiratokat. Az alkalmazás nem tölt fel médiafájlokat vagy átiratokat semmilyen átírási szolgáltatásba. Nincs beépített analitikája, fiókrendszere, hirdetése vagy automatikus összeomlásjelentés-küldése.

## A számítógépen tárolt fájlok
A projektek tartalmazhatják a forrásmédia elérési útját és hash-értékét, a normalizált hanganyagot, a motorok nyers kimenetét, az átiratokat, az átnézés során Ön által végzett módosításokat, az ellenőrzőpontokat, a hardvermérések eredményeit és az alkalmazásnaplókat. A modellek, a részleges letöltések, az ellenőrzések eredményét rögzítő fájlok és a beállítások helyben kerülnek mentésre. Ezek a fájlok a felvételekből vagy a fájlok elérési útjából származó személyes adatokat is tartalmazhatnak. Az alkalmazás nem titkosítja őket.

A telepítőcsomaggal terjesztett verziók a korábbi LocalAppData/TriASR könyvtárat használják, hacsak nem választ más projekt- vagy modellmappát. Ez megőrzi a meglévő telepítéseket. Az alkalmazás eltávolítása a projekteket, a beállításokat és a letöltött modelleket a helyükön hagyja. Adatai eltávolításához zárja be az alkalmazást, és törölje saját kezűleg a kiválasztott mappákat. A megtartani kívánt felvételeket és exportokat előtte mentse el.

## Hálózati kapcsolatok
Amikor modell- vagy futtatókörnyezet-letöltést kér, az alkalmazás a Hugging Face vagy a GitHub szolgáltatásához és azok letöltési infrastruktúrájához csatlakozik. Ezek a szolgáltatók a szokásos kapcsolati adatokat kapják meg, például az Ön IP-címét és a kért fájlt. A média- és átirattartalmak nem részei ezeknek a letöltési kéréseknek. A szolgáltatók saját szolgáltatásaira a saját adatvédelmi szabályzatuk vonatkozik:
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Frissítések keresése
Hacsak ki nem kapcsolja a Beállításokban, az alkalmazás indításkor, legfeljebb 12 óránként, valamint a Frissítések keresése most lehetőség kiválasztásakor egy kis verziófájlt (latest.json) kér le a GitHubról. A GitHub a szokásos kapcsolati adatokat kapja meg, például az Ön IP-címét, a kérés pedig az alkalmazás nevét és verzióját tartalmazza. Felvételek, átiratok, projektadatok, hardveradatok vagy azonosítók nem kerülnek elküldésre. Az alkalmazás soha nem telepít frissítést magától: Ön választja a Frissítés most lehetőséget, és az alkalmazás a letöltött telepítő SHA256-ellenőrzőösszegét futtatás előtt összeveti a közzétett értékkel.

A helyi javítókiszolgáló a loopback-csatolón (127.0.0.1) keresztül kommunikál; ez nem felhőalapú következtetés. Az alkalmazás telepítőjének letöltése szintén a GitHubhoz csatlakozik. A szükséges modellek és futtatókörnyezetek telepítése után az átírás offline is működhet.

## Terminál, exportálás és támogatás
A PowerShell panelen megadott parancsok az Ön Windows-felhasználójának jogosultságaival futnak. A futtatott parancsoktól függően hozzáférhetnek fájlokhoz, kapcsolatba léphetnek hálózati szolgáltatásokkal vagy adatokat továbbíthatnak. A jelen nyilatkozat helyi feldolgozásról szóló kijelentése az alkalmazás átírási funkcióira vonatkozik, nem tetszőleges terminálparancsokra.

Az alkalmazás nem küld el automatikusan naplókat. Mielőtt GitHub-hibajegyekben megosztaná őket, nézze át a diagnosztikát, a motorok kimenetét és az átiratokat. Az adatok másolását vagy exportálását Ön kezdeményezi. Az operációs rendszer, a biztonsági mentések, a szinkronizált mappák, a biztonsági szoftverek és a külső futtatókörnyezetek a saját szabályzataik szerint kezelhetik a fájlokat.

## Változások és kérdések
A jövőbeli kiadások módosíthatják ezt a nyilatkozatot. Mindig a telepített verzióhoz mellékelt nyilatkozatot tekintse át. Adatvédelmi kérdéseit a projekt GitHub-hibajegyein keresztül teheti fel, személyes felvételek, átiratok vagy naplók csatolása nélkül.
