# Politique de confidentialité de Mockingbird Studio

Date d'entrée en vigueur : 1er octobre 2026

## Traitement local
Mockingbird Studio traite les enregistrements, la reconnaissance vocale et la correction des transcriptions sur votre ordinateur. L'application n'envoie ni fichiers multimédias ni transcriptions à un service de transcription. Elle ne comporte aucun outil d'analyse d'audience intégré, aucun système de compte, aucune publicité et aucun envoi automatique de rapports de plantage.

## Fichiers stockés sur votre ordinateur
Les projets peuvent contenir le chemin et le hachage du fichier multimédia source, l'audio normalisé, la sortie brute des moteurs, les transcriptions, les révisions effectuées lors de la relecture, des points de reprise, des mesures matérielles et les journaux de l'application. Les modèles, les téléchargements partiels, les reçus de vérification et les paramètres sont enregistrés localement. Ces fichiers peuvent contenir des informations personnelles provenant de vos enregistrements ou des chemins de fichiers. Ils ne sont pas chiffrés par l'application.

Les versions packagées utilisent l'ancien répertoire LocalAppData/TriASR, sauf si vous choisissez un autre dossier de projets ou de modèles. Cela préserve les installations existantes. La désinstallation de l'application laisse en place les projets, les paramètres et les modèles téléchargés. Pour supprimer vos données, fermez l'application et supprimez vous-même les dossiers choisis. Conservez les enregistrements ou les exports que vous souhaitez garder.

## Connexions réseau
Lorsque vous demandez le téléchargement de modèles ou de runtimes, l'application se connecte à Hugging Face ou à GitHub et à leur infrastructure de téléchargement. Ces fournisseurs reçoivent les informations de connexion habituelles, telles que votre adresse IP et la ressource demandée. Le contenu des fichiers multimédias et des transcriptions n'est pas inclus dans ces demandes de téléchargement. Les politiques de confidentialité des fournisseurs s'appliquent à leurs services :
- https://huggingface.co/privacy
- https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement

### Recherche de mises à jour
Sauf si vous la désactivez dans Paramètres, l'application demande à GitHub un petit fichier de version (latest.json) à son démarrage, au plus toutes les 12 heures, et chaque fois que vous choisissez Rechercher les mises à jour maintenant. GitHub reçoit les informations de connexion habituelles, telles que votre adresse IP, et la demande contient le nom et la version de l'application. Aucun enregistrement, transcription, donnée de projet, détail matériel ou identifiant n'est envoyé. L'application n'installe jamais une mise à jour d'elle-même : c'est vous qui choisissez Mettre à jour maintenant, et l'installateur téléchargé est vérifié par rapport à sa somme de contrôle SHA256 publiée avant d'être exécuté.

Le serveur de correction local communique via l'interface de bouclage (127.0.0.1) ; il ne s'agit pas d'une inférence dans le cloud. Les téléchargements d'installation depuis GitHub contactent également GitHub. Une fois les modèles et les runtimes nécessaires installés, la transcription peut fonctionner hors ligne.

## Terminal, exports et assistance
Les commandes que vous saisissez dans le panneau PowerShell disposent des autorisations de votre utilisateur Windows. Elles peuvent accéder à des fichiers, contacter des services réseau ou transmettre des données selon les commandes que vous exécutez. La déclaration de traitement local de cette politique décrit les fonctions de transcription de l'application, pas les commandes arbitraires du terminal.

L'application n'envoie pas automatiquement de journaux. Relisez les diagnostics, la sortie des moteurs et les transcriptions avant de les partager dans les tickets GitHub. La copie ou l'export de données se fait à votre initiative. Votre système d'exploitation, vos sauvegardes, vos dossiers synchronisés, vos logiciels de sécurité et les runtimes tiers peuvent traiter les fichiers selon leurs propres politiques.

## Modifications et questions
Les versions futures pourront réviser cette politique. Consultez la politique fournie avec la version que vous installez. Les questions relatives à la confidentialité peuvent être posées via les tickets GitHub du projet, sans y joindre d'enregistrements, de transcriptions ou de journaux privés.
