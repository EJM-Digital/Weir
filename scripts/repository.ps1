# Where Weir's source and releases live: the one place the repository's PowerShell scripts write the owner and
# name, dot-sourced by them. The server, the tray, the web app and the Node scripts each keep their own copy, and
# scripts/check-repository-owner.mjs fails when any of them differs or the name is written anywhere else.
$WeirRepository = 'EJM-Digital/Weir'
$WeirRepositoryUrl = "https://github.com/$WeirRepository"
$WeirApiRepositoryUrl = "https://api.github.com/repos/$WeirRepository"
