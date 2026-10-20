<#
    The scenarios, in run order: what each one does and what it takes to pass. Data only, so the orchestrator, the runner,
    the plan mode, the record and the tests (scripts/scenarios.test.mjs) all read the same list.

    Phase `Update` runs FIRST, with no checkpoint restore, on the VM with Deluno set up and Weir installed by Deluno's picker (the
    previous release): it installs the build under test over that Weir. Deluno's suite then makes its hand-offs, films, resends and
    outages while the build under test is the Weir installed, because a hand-off the previous release handled says nothing about
    the build. Phase `WithDeluno` runs SECOND, with no restore, on the VM exactly as Deluno's suite leaves it, with the key file for
    Deluno's API in the VM. Phase `Fresh` runs LAST on the clean checkpoint, with the build under test installed alone. One
    invocation can run them back to back, or the Update phase alone before Deluno's suite and the other two after it.

    Every scenario is required. A scenario whose preconditions are not met is recorded `not-applicable` with the reason, and a
    record with any not-applicable scenario sets no success status: a release cannot pass without the real-Deluno scenarios.

    Every scenario runs alone (Invoke-WeirScenarios.ps1 -Scenarios <id>): it makes what it needs itself and reads nothing an
    earlier scenario left. The Fresh scenarios start from Initialize-FreshScenario (the build under test installed and running, an
    account the run holds, workflows named for the run); every scenario ends by checking that the logs gained no unexpected warning
    or error since it began, so there is no scenario of its own for the logs.
#>

$ScenarioCatalog = @(
    [pscustomobject]@{
        Id = 'update-over-installed-weir'; Phase = 'Update'; Required = $true
        Title = 'An update over the Weir Deluno installed'
        Does = 'When Deluno''s install log shows its picker at work, waits for it to say it has finished installing; then takes the Weir it installed (the previous release, running), signs in (or creates the account when it has none), makes a Weir-only workflow and has Weir clean a film in it, reads every Activity entry, then installs the build under test over the running Weir with Setup --silent and reads every Activity entry again. Asks the updated server for the copy of Weir''s data the tray asks for before an update. Leaves a note of the moment the build under test began answering, for the Deluno scenarios.'
        Passes = 'Weir is running again on the build under test, with its commit, without being started by hand; the tray log says it was running before the install and started again; the account, the workflow and every Activity entry from before (all of them, paged, with the same time, kind, title, detail and result) are still there; when the update changed the database the copy saved before it exists and System overview names it; the updated server saves the tray''s requested copy under backups\pre-update; a film dropped in afterwards is cleaned.'
    }
    [pscustomobject]@{
        Id = 'workflows-from-deluno'; Phase = 'WithDeluno'; Required = $true
        Title = 'Workflows set up from Deluno'
        Does = 'Gets an API key for the Deluno in the VM from the key file the Deluno session left there (read inside the VM, then deleted), connects Weir to Deluno, or finds the connection Deluno''s Connect Weir already made, and waits for Weir to set up its workflows from Deluno''s libraries and folders. A Deluno with no library set to refine before import has none to set up, and the scenario says so.'
        Passes = 'Weir''s workflows are linked to Deluno with their folders from Deluno and the folder chain check shows every step ready, with nothing typed in Weir. With no library in Deluno set to refine before import the scenario is not applicable and says what Deluno''s suite has to set up.'
    }
    [pscustomobject]@{
        Id = 'deluno-film-handoff'; Phase = 'WithDeluno'; Required = $true
        Title = 'A film Deluno handed over'
        Does = 'Lists the hand-offs Deluno really made (its own API, with the key) and follows each film the build under test took (made after it began answering) through Weir. Films made before that were handled by the previous release and are not judged.'
        Passes = 'At least one film hand-off was made after the build under test took over and finished; for every such film, Weir shows the cleaned file as processed with Deluno''s imported answer recorded against it, and Deluno shows its own import done. With none made after the build took over, the scenario is not applicable and says what Deluno''s suite has to do.'
    }
    [pscustomobject]@{
        Id = 'deluno-release-with-extra'; Phase = 'WithDeluno'; Required = $true
        Title = 'A release with a small extra'
        Does = 'Looks, among the film hand-offs the build under test took, for a release whose folder in the download client still holds a film and a video file under the minimum size of the workflow Weir cleaned the film in. Deluno leaves such an extra out of the hand-off, so the extra is read where it stays.'
        Passes = 'Such a release exists; its film was cleaned; the extra is still in the client''s folder, Weir wrote nothing named for it to its output folder, and Weir either never saw it or left it alone as skipped; nothing in the workflow is failed or rejected.'
    }
    [pscustomobject]@{
        Id = 'deluno-resend'; Phase = 'WithDeluno'; Required = $true
        Title = 'A hand-off sent again from Deluno'
        Does = 'Asks Deluno to tell Weir again what became of an imported hand-off (Deluno''s own send-again), then reads both sides.'
        Passes = 'Deluno really sends it again (a new attempt, just now) and records the repeat as delivered or settled, never refused; Weir keeps the one imported answer and the file it already settled, and raises no failure.'
    }
    [pscustomobject]@{
        Id = 'deluno-outcomes'; Phase = 'WithDeluno'; Required = $true
        Title = 'What Deluno was told about every outcome'
        Does = 'Reads the outcome Deluno recorded for each hand-off and, for the hand-offs made after the build under test took over, the outcome Weir recorded for the same file.'
        Passes = 'No outcome is pending, refused, never received or given up on, except a hand-off that handed no file back (Weir was down for it), which Deluno closes as not-imported and given-up with nothing owed and says so; for every hand-off the build under test took and Deluno delivered, imported on one side is imported on the other, and not-imported on one is not-imported on the other.'
    }
    [pscustomobject]@{
        Id = 'fresh-install'; Phase = 'Fresh'; Required = $true
        Title = 'Fresh install'
        Does = 'Runs Weir-win-Setup.exe --silent on the clean machine, as another program installing Weir does, then starts the installed Weir.exe --port 9347 --silent.'
        Passes = 'Setup exits 0; Weir answers /ready; the installed server reports the version under test, with its commit; the tray and the server are running; the server listens on this PC only; the tray shows no window; the tray log says it started.'
    }
    [pscustomobject]@{
        Id = 'first-visit-account'; Phase = 'Fresh'; Required = $true
        Title = 'First visit and account creation'
        Does = 'Starts the installed Weir with no account (setting it back to none when it has one), opens it in a brand-new Microsoft Edge profile (headless), creates the account through Weir''s API, then opens Weir again in another brand-new profile, and signs in.'
        Passes = 'The first page is account creation and the second is the sign-in page; neither ever says a session expired; the new account signs in and Weir knows who it is.'
    }
    [pscustomobject]@{
        Id = 'weir-only-film'; Phase = 'Fresh'; Required = $true
        Title = 'A film dropped into a Weir-only workflow'
        Does = 'Starts the installed Weir and signs in (creating the account when it offers to), makes a small film (video, three audio languages, two subtitle tracks, an .nfo beside it), makes a workflow with no manager over folders of its own, drops the release into its watched folder and asks Weir to look.'
        Passes = 'Weir cleans the film: the cleaned file is in the output folder with the video and the English audio kept and the other audio and the subtitles removed, and is smaller; the original and its .nfo are still where they were; Activity says it was processed.'
    }
    [pscustomobject]@{
        Id = 'process-again'; Phase = 'Fresh'; Required = $true
        Title = 'Process again'
        Does = 'Starts the installed Weir and signs in (creating the account when it offers to), has Weir clean a film in a workflow of its own, then asks Weir to process that film again, as Process again does in the file''s drawer.'
        Passes = 'The answer says Weir already cleaned this file and skipped it; Activity gets a "Skipped: already done" line; the output is not rewritten and no second job is made.'
    }
    [pscustomobject]@{
        Id = 'pause-resume'; Phase = 'Fresh'; Required = $true
        Title = 'Pause and resume from the tray'
        Does = 'Starts the installed Weir and signs in (creating the account when it offers to), makes a workflow of its own, writes the tray''s pause-request file to ask Weir to pause until resumed, with "keep looking for new files" on, drops a film into the watched folder during the pause, watches for 20 seconds, then writes the resume request.'
        Passes = 'Weir takes the request and deletes the file; Pause reads paused; Weir finds the film and holds it, and nothing is cleaned, written or removed while paused; after the resume request it is processed once; Activity shows Paused and Resumed by the tray.'
    }
    [pscustomobject]@{
        Id = 'deleted-queued-file'; Phase = 'Fresh'; Required = $true
        Title = 'A deleted file that was waiting'
        Does = 'Starts the installed Weir and signs in (creating the account when it offers to), makes a workflow of its own, puts a file in its watched folder, lets Weir find it and wait for it to settle, deletes it, and lets Weir look again.'
        Passes = 'Weir says the file is no longer there and stops waiting for it; it is never reported as failed or needing the person, and adds no warning or error to the log.'
    }
    [pscustomobject]@{
        Id = 'update-requests'; Phase = 'Fresh'; Required = $true
        Title = 'The update buttons'
        Does = 'Starts the installed Weir and signs in (creating the account when it offers to), then presses Check for updates and Download update the way System > About does (their requests to Weir), with the real tray running.'
        Passes = 'Weir offers the buttons only while the tray is alive; each request leaves its flag file, the tray takes it, and the update state comes to rest with an answer (nothing newer, an update found, or a plain failure message) rather than staying on checking or downloading.'
    }
    [pscustomobject]@{
        Id = 'recover-locked-out-account'; Phase = 'Fresh'; Required = $true
        Title = 'Recovering a locked-out account'
        Does = 'Starts the installed Weir and signs in (creating the account when it offers to), saves a setting, signs in a second browser, stops Weir, and runs the installed WeirServer.exe recover as a person''s console does, with no variable set: its help, its account list, then a reset of the account''s password. Starts Weir again.'
        Passes = 'The command finds the data folder the installed server uses, lists the account as an active admin, resets the password and says how many signed-in sessions it ended; afterwards both earlier sessions are refused, the forgotten password is refused, the new password signs in, the saved setting is as it was and Activity says the password was recovered from the server.'
    }
)
