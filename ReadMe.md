DesktopAnalytics.net
===============================

[Segment.IO](http://segment.io) provides a nice <i>server-oriented</i> library in [Analytics.net](https://github.com/segmentio/Analytics.NET). This project is just one little class that wraps that to provide some standard behavior needed by <i>desktop</i> applications:

+ Supplies guids for user ids, saves that in a Settings file
+ Looks up the machine's external IP address and sends that along
+ Set $Browser to the Operating System Version (e.g. "Windows 8").
+ Auto events
 + First launch ("Create" to fit MixPanel's expectation)
 + Version Upgrade

##Install
    PM> Install-Package DesktopAnalytics
 
##Usage

###Initialization
```c#
    using (new Analytics("mySegmentIOSecret"), userInfo)
	{	
		// other setup, and eventually
		Application.Run();
	}
	
```

If you want to go beyond anonymous users, you can feed information about your user into DA like this:

```c#
var userInfo = new UserInfo()
				{
					FirstName = "John",
					LastName = "Smith",
					Email="john@example.com",
					UILanguageCode= "fr"
				};
			userInfo.OtherProperties.Add("FavoriteColor","blue");

    using (new Analytics("mySegmentIOSecret"), userInfo)
	{	
		// other setup, and eventually
		Application.Run();
	}
```

If you have a way of letting users (or testers) disable tracking, pass that value as the second argument:

```c#
using (new Analytics("mySegmentIOSecret", allowTracking))
```

In this example, we use an environment variable so that testers and developers don't get counted:

```c#
#if DEBUG
	//always track if this is a debug built, but track to a different segment.io project
	using (new Analytics("(the secret for the debug version)"))
				
#else
	// if this is a release build, then allow an envinroment variable to be set to false
	// so that testers aren't generating false analytics
	string feedbackSetting = System.Environment.GetEnvironmentVariable("FEEDBACK");
		        
	var allowTracking = string.IsNullOrEmpty(feedbackSetting) || feedbackSetting.ToLower() == "yes" || feedbackSetting.ToLower() == "true";

	using (new Analytics("(the secret for the release version)", RegistrationDialog.GetAnalyticsUserInfo(), allowTracking))
	{
		// other setup, and eventually
		Application.Run();
	}
		        
#endif
```

###Tracking

Wherever you want to register that something happened, call Track on the static object named "Analytics":

```c#
Analytics.Track("Create New Image");
```

If you have properties you need to record, add them by passing in a Dictionary<string, string>, like this:

```c#
Analytics.Track("Save PDF", new Dictionary<string, string>() {
			{"PageCount",  pageCount}, 
			{"Layout", "A4Landscape"}
        });
```

###Error Reporting

    Analytics.ReportException(error);
    
If you've also got LibPalaso in your app, hook up its ExceptionHandler like this:

    ExceptionHandler.AddDelegate((w,e) => DesktopAnalytics.Analytics.ReportException(e.Exception));
   

##Dependencies

The project is currently built for .net 4 client profile. If you get the solution, nuget should auto-restore the two dependencies when you build; they are not part of the source tree.

##License

MIT Licensed
(As are the dependencies, Analytics.Net and Json.Net).

##Roadmap
Add user notification of tracking and opt-out UI.

Add opt-in user identification (e.g., email)
