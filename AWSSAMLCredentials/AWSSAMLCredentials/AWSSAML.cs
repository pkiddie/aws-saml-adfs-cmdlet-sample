﻿/*
 * Copyright 2010-2013 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Runtime.InteropServices;
using System.Security.Principal;

using System.Management.Automation;
using System.Collections.ObjectModel;
using System.Management.Automation.Runspaces;

using System.Net;

using Amazon;
using Amazon.SecurityToken;
using Amazon.Runtime;

namespace AWSSAML
{
    [Cmdlet(VerbsCommon.Set, "AWSSAMLCredentials")]
    public class SetAWSSAMLCredentials : PSCmdlet
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(String lpszUsername, String lpszDomain, String lpszPassword,
            int dwLogonType, int dwLogonProvider, ref IntPtr phToken);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private extern static bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private extern static bool DuplicateToken(IntPtr ExistingTokenHandle,
            int SECURITY_IMPERSONATION_LEVEL, ref IntPtr DuplicateTokenHandle);

        private IntPtr tokenHandle = new IntPtr(0);
        private IntPtr dupeTokenHandle = new IntPtr(0);
        private WindowsImpersonationContext impersonatedUser;

        private string identityProviderUrl;
        [Parameter(
            Mandatory=true,
            ValueFromPipeline=true,
            ValueFromPipelineByPropertyName=true,
            Position=0,
            HelpMessage="The identity provider URL."
        )]
        public string IdentityProviderUrl
        {
            get { return identityProviderUrl; }
            set { identityProviderUrl = value; }
        }

        private bool useCurrentCredentials = true;
        [Parameter(
            Mandatory = false,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            Position = 1,
            HelpMessage = "Use current user Windows credentials."
        )]
        public bool UseCurrentCredentials
        {
            get { return useCurrentCredentials; }
            set { useCurrentCredentials = value; }
        }

        private int roleIndex = Int16.MaxValue;
        private string userName;
        private string password;
        private string role;

        [Parameter(
            Mandatory = false,
            ValueFromPipeline = true,
            ValueFromPipelineByPropertyName = true,
            Position = 2,
            HelpMessage = "Role index to use."
        )]
        public int RoleIndex
        {
            get { return roleIndex; }
            set { roleIndex = value; }
        }

        [Parameter(
           Mandatory = false,
           ValueFromPipeline = true,
           ValueFromPipelineByPropertyName = true,
           Position = 3,
           HelpMessage = "ADFS username"
       )]
        public string Username
        {
            get { return userName; }
            set { userName = value; }
        }

        [Parameter(
          Mandatory = false,
          ValueFromPipeline = true,
          ValueFromPipelineByPropertyName = true,
          Position = 4,
          HelpMessage = "ADFS password"
      )]
        public string Password
        {
            get { return password; }
            set { password = value; }
        }

        [Parameter(
          Mandatory = false,
          ValueFromPipeline = true,
          ValueFromPipelineByPropertyName = true,
          Position = 3,
          HelpMessage = "AWS role"
      )]
        public string Role
        {
            get { return role; }
            set { role = value; }
        }

        protected override void ProcessRecord()
        {
            try
            {
                AWSSAMLUtils awsSamlUtils = new AWSSAMLUtils();
                SessionAWSCredentials awsSessionCredentials = null;

                ICredentials userCredentials = GetUserCredentials(useCurrentCredentials);

                Uri uri = new Uri(identityProviderUrl);
                NetworkCredential networkCredentials = userCredentials.GetCredential(uri, "");
                if (CredentialCache.DefaultCredentials != userCredentials)
                {
                    ImpersonateUser(networkCredentials.UserName, networkCredentials.Password, networkCredentials.Domain);
                }

                string samlAssertion = awsSamlUtils.GetSamlAssertion(identityProviderUrl);
                string[] awsSamlRoles = awsSamlUtils.GetAwsSamlRoles(samlAssertion);
                UnImpersonateUser();

                string awsSamlRole = null;
                if (roleIndex < awsSamlRoles.Length)
                {
                    awsSamlRole = awsSamlRoles[roleIndex];
                }
                else if (!string.IsNullOrEmpty(role))
                {
                    awsSamlRole = awsSamlRoles.FirstOrDefault(p => p.Contains(role));
                    if(awsSamlRole == null) throw new ArgumentException(string.Format("role {0} not found in list of available roles: {1}",role, string.Join(", ",awsSamlRoles)));
                }
                else
                {
                    awsSamlRole = AskUserForAwsSamlRole(awsSamlRoles);
                }

                awsSessionCredentials = awsSamlUtils.GetSamlRoleCredentails(samlAssertion, awsSamlRole);
                SetPowershellSamlProfile(awsSessionCredentials.GetCredentials());
            }
            catch
            {
                throw;
            }
        }

        private ICredentials GetUserCredentials(bool useCurrentCredentials)
        {
            if (useCurrentCredentials)
            {
                return CredentialCache.DefaultCredentials;
            }

            if (!(string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password)))
            {
                var domain = string.Empty;

                if (!userName.Contains('\\')) return new NetworkCredential(userName, password, domain);

                var domainAndUser = userName.Split(new[] {'\\'}, 2);
                domain = domainAndUser[0];
                userName = domainAndUser[1];

                return new NetworkCredential(userName, password, domain);
            }

            return AskUserForCredentials();
        }

        private ICredentials AskUserForCredentials()
        {
            Console.Write("username: ");
            var user = Console.ReadLine();

            Console.Write("password: ");
            var pass = GetPasswordViaConsole();
            Console.WriteLine();

            Console.Write("domain: ");
            var domain = Console.ReadLine();

            return new NetworkCredential(user, pass, domain);
        }

        private string AskUserForAwsSamlRole(string[] awsSamlRoles)
        {
            Console.WriteLine("Please choose the role you would like to assume:");
            for (int i = 0; i < awsSamlRoles.Length; i++)
            {
                string[] awsSamlRole = awsSamlRoles[i].Split(',');
                Console.WriteLine(String.Format(" [{0}]: {1} ", i, awsSamlRole[1]));
            }

            Console.Write("Selection: ");
            
            ConsoleKeyInfo key;
            int index = 0;

            do
            {
                key = Console.ReadKey();
                index = int.Parse(key.KeyChar.ToString());
            } while (!Char.IsNumber(key.KeyChar) || index > awsSamlRoles.Length - 1);
            Console.WriteLine();

            return awsSamlRoles[index];
        }

        private string GetPasswordViaConsole()
        {
            string password = "";
            ConsoleKeyInfo key;

            do
            {
                key = Console.ReadKey(true);

                if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
                {
                    password += key.KeyChar;
                    Console.Write("*");
                }
                else
                {
                    if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                    {
                        password = password.Substring(0, (password.Length - 1));
                        Console.Write("\b \b");
                    }
                }
            }
            while (key.Key != ConsoleKey.Enter);

            return password;
        }

        private void SetPowershellSamlProfile(ImmutableCredentials awsSessionCredentials)
        {
            // create a pipeline and feed it the script text
            string script = String.Format("Set-AWSCredentials -AccessKey '{0}' -SecretKey '{1}' -SessionToken '{2}'", awsSessionCredentials.AccessKey, awsSessionCredentials.SecretKey, awsSessionCredentials.Token);

            Runspace theRunSpace = System.Management.Automation.Runspaces.Runspace.DefaultRunspace;

            if (theRunSpace.RunspaceStateInfo.State == RunspaceState.Opened)
            {
                using (Pipeline thePipeline = theRunSpace.CreateNestedPipeline(script, true))
                {
                    Collection<PSObject> theRetVal = thePipeline.Invoke();
                }
            }
        }

        private void ImpersonateUser(string userName, string password, string domainName)
        {
            const int LOGON32_TYPE_NEW_CREDENTIALS = 9;
            const int LOGON32_PROVIDER_WINNT50 = 3;
            const int SecurityImpersonation = 2;

            tokenHandle = IntPtr.Zero;
            dupeTokenHandle = IntPtr.Zero;

            // Call LogonUser to obtain a handle to an access token.
            // If domain joined 
            bool returnValue = LogonUser(userName, domainName, password, LOGON32_TYPE_NEW_CREDENTIALS,
                                            LOGON32_PROVIDER_WINNT50, ref tokenHandle);


            if (!returnValue)
            {
                int ret = Marshal.GetLastWin32Error();
                const int errorCode = 0x5; //ERROR_ACCESS_DENIED
                throw new System.ComponentModel.Win32Exception(errorCode);
            }

            returnValue = DuplicateToken(tokenHandle, SecurityImpersonation, ref dupeTokenHandle);

            if (!returnValue)
            {
                CloseHandle(tokenHandle);
                //Exception thrown in trying to duplicate token.
                return;
            }

            // The token that is passed to the following constructor must be a primary token in order to use it for impersonation.
            WindowsIdentity newId = new WindowsIdentity(dupeTokenHandle);
            impersonatedUser = newId.Impersonate();
        }

        private void UnImpersonateUser()
        {
            if (impersonatedUser != null)
            {
                impersonatedUser.Undo();
            }

            if (tokenHandle != IntPtr.Zero)
            {
                CloseHandle(tokenHandle);
            }

            if (dupeTokenHandle != IntPtr.Zero)
            {
                CloseHandle(dupeTokenHandle);
            }
        }
    }
}
